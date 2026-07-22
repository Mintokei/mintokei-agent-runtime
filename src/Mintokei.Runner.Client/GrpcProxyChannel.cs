using System.Net.Sockets;
using System.Text;
using Grpc.Net.Client;

namespace Mintokei.Runner;

/// <summary>
/// Builds a <see cref="GrpcChannel"/> that tunnels through an HTTP <c>CONNECT</c> proxy when <c>HTTPS_PROXY</c>
/// is set. <c>Grpc.Net.Client</c>'s DEFAULT channel uses a raw-socket connectivity subchannel that <b>ignores
/// the ambient proxy</b> (unlike <see cref="HttpClient"/>/<c>ClientWebSocket</c>), so a runner on a
/// deny-by-default network — e.g. the sandbox broker's <c>--internal</c> net, where the broker is the ONLY route
/// out — can never establish its gRPC control stream and never comes online.
///
/// The fix is a <see cref="SocketsHttpHandler.ConnectCallback"/> that dials the proxy and performs the CONNECT
/// handshake to the target itself. Two things are load-bearing: (1) setting <c>ConnectCallback</c> moves gRPC
/// onto the connection-pool path (which uses the callback) instead of the proxy-blind subchannel transport; and
/// (2) <c>UseProxy = false</c> — otherwise the handler ALSO tunnels via the ambient proxy, double-CONNECTing.
/// </summary>
public static class GrpcProxyChannel
{
    /// <summary><c>HTTPS_PROXY</c> / <c>https_proxy</c> from the environment, or null when unset/invalid.</summary>
    public static Uri? ResolveHttpsProxy()
    {
        var value = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                 ?? Environment.GetEnvironmentVariable("https_proxy");
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>A channel to <paramref name="address"/> that tunnels through <paramref name="proxy"/> when
    /// non-null (otherwise a plain channel with default behaviour).</summary>
    public static GrpcChannel ForAddress(string address, Uri? proxy)
    {
        if (proxy is null)
            return GrpcChannel.ForAddress(address);

        var handler = new SocketsHttpHandler
        {
            UseProxy = false, // we CONNECT-tunnel manually below; the built-in proxy would double-tunnel over it
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(proxy.Host, proxy.Port, ct);
                    var stream = new NetworkStream(socket, ownsSocket: true);
                    await TunnelAsync(stream, context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
                    return stream; // SocketsHttpHandler layers TLS + HTTP/2 on top of the tunnel
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
    }

    /// <summary>Perform the HTTP <c>CONNECT</c> handshake over <paramref name="stream"/> for
    /// <paramref name="host"/>:<paramref name="port"/>. Throws on a non-2xx proxy response. Public for tests.</summary>
    public static async Task TunnelAsync(Stream stream, string host, int port, CancellationToken ct)
    {
        var target = $"{host}:{port}";
        var request = $"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);

        var statusLine = await ReadLineAsync(stream, ct);           // e.g. "HTTP/1.1 200 Connection Established"
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !parts[1].StartsWith('2'))
            throw new IOException($"proxy CONNECT to {target} failed: {statusLine}");

        string line;
        do { line = await ReadLineAsync(stream, ct); } while (line.Length > 0); // drain headers to the blank line
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0 || one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') sb.Append((char)one[0]);
            if (sb.Length > 8192) break;
        }
        return sb.ToString();
    }
}
