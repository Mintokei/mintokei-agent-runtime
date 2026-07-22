using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mintokei.Sandbox.Broker;

/// <summary>
/// A minimal HTTP <c>CONNECT</c> forward proxy that only tunnels to allowlisted hosts — the egress layer of the
/// per-session sandbox broker (the runtime's <c>SandboxEgress.Broker</c> mode). It performs <b>no MITM</b>:
/// after the allowlist check it opens an opaque TCP tunnel, so the sandbox's TLS to git / registries / the model
/// API stays end-to-end; denied hosts get <c>403</c> and no upstream connection is made.
///
/// The sandbox reaches this via <c>HTTP(S)_PROXY</c>. On the per-session <c>--internal</c> network the proxy is
/// the container's only route out, so this allowlist is the effective egress boundary (a process that ignores
/// the proxy env still has nowhere to go). Credential injection (git tokens, model-API key) layers on top in a
/// later slice; this is egress-only.
/// </summary>
public sealed class AllowlistForwardProxy
{
    private readonly List<string> _exact = [];
    private readonly List<string> _suffix = []; // ".example.com" → any subdomain (and the bare "example.com")
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<int> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AllowlistForwardProxy(IReadOnlyCollection<string> allowlist, ILogger? logger = null)
    {
        foreach (var raw in allowlist)
        {
            var e = raw.Trim().ToLowerInvariant();
            if (e.Length == 0) continue;
            (e.StartsWith('.') ? _suffix : _exact).Add(e);
        }
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Resolves to the actually-bound listen port once <see cref="RunAsync"/> has started (useful when
    /// binding to an ephemeral port 0, e.g. in tests).</summary>
    public Task<int> BoundPort => _bound.Task;

    /// <summary>Whether a CONNECT target host is permitted: an exact allowlist entry, or a <c>.suffix</c> entry
    /// that matches the host or any of its subdomains. Case-insensitive. Pure — usable without a running proxy.</summary>
    public bool IsAllowed(string host)
    {
        host = host.Trim().ToLowerInvariant();
        foreach (var e in _exact)
            if (host == e) return true;
        foreach (var s in _suffix)
            if (host == s[1..] || host.EndsWith(s, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Convenience one-shot evaluation of <paramref name="host"/> against <paramref name="allowlist"/>.</summary>
    public static bool IsAllowed(IReadOnlyCollection<string> allowlist, string host)
        => new AllowlistForwardProxy(allowlist).IsAllowed(host);

    /// <summary>Listen on <paramref name="port"/> (0 = ephemeral) and serve CONNECT tunnels until cancelled.</summary>
    public async Task RunAsync(int port, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        _bound.TrySetResult(((IPEndPoint)listener.LocalEndpoint).Port);
        _logger.LogInformation("sandbox broker proxy listening on :{Port} ({Exact} exact + {Suffix} suffix allow-rules)",
            ((IPEndPoint)listener.LocalEndpoint).Port, _exact.Count, _suffix.Count);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }
                _ = HandleClientAsync(client, ct); // one tunnel per connection; failures are contained per-client
            }
        }
        finally { listener.Stop(); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                var (method, target) = await ReadRequestHeadAsync(stream, ct);
                if (!string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStatusAsync(stream, "501 Not Implemented", ct); // egress layer only tunnels CONNECT
                    return;
                }

                var (host, upstreamPort) = SplitHostPort(target);
                if (!IsAllowed(host))
                {
                    _logger.LogWarning("sandbox broker DENY {Host}:{Port}", host, upstreamPort);
                    await WriteStatusAsync(stream, "403 Forbidden", ct);
                    return;
                }

                using var upstream = new TcpClient { NoDelay = true };
                try { await upstream.ConnectAsync(host, upstreamPort, ct); }
                catch
                {
                    await WriteStatusAsync(stream, "502 Bad Gateway", ct);
                    return;
                }

                await WriteStatusAsync(stream, "200 Connection Established", ct);
                _logger.LogInformation("sandbox broker ALLOW {Host}:{Port}", host, upstreamPort);

                // Opaque bidirectional pump — no MITM. Either side closing tears down the tunnel.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var up = upstream.GetStream();
                var a = PumpAsync(stream, up, linked.Token);
                var b = PumpAsync(up, stream, linked.Token);
                await Task.WhenAny(a, b);
                await linked.CancelAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "sandbox broker connection error");
            }
        }
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        try { await from.CopyToAsync(to, ct); }
        catch { /* peer closed / cancelled — the sibling pump + dispose end the tunnel */ }
    }

    // Read the CONNECT request line, then drain the header block to the blank line. The client waits for our 200
    // before sending any TLS, so consuming up to the blank line never eats tunnel bytes.
    private static async Task<(string Method, string Target)> ReadRequestHeadAsync(Stream stream, CancellationToken ct)
    {
        var line = await ReadLineAsync(stream, ct);
        string header;
        do { header = await ReadLineAsync(stream, ct); } while (header.Length > 0);

        var parts = line.Split(' ', 3);
        return parts.Length >= 2 ? (parts[0], parts[1]) : ("", "");
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0 || one[0] == (byte)'\n') break; // EOF or line end
            if (one[0] != (byte)'\r') sb.Append((char)one[0]);
            if (sb.Length > 8192) break; // header line guard
        }
        return sb.ToString();
    }

    private static (string Host, int Port) SplitHostPort(string target)
    {
        var i = target.LastIndexOf(':');
        if (i > 0 && int.TryParse(target.AsSpan(i + 1), out var p)) return (target[..i], p);
        return (target, 443);
    }

    private static async Task WriteStatusAsync(Stream stream, string status, CancellationToken ct)
        => await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n\r\n"), ct);
}
