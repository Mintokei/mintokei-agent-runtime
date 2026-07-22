using System.Net;
using System.Net.Sockets;
using System.Text;
using Mintokei.Runner;
using Xunit;

namespace Mintokei.Runner.Client.Tests;

public class GrpcProxyChannelTests
{
    [Fact]
    public void ResolveHttpsProxy_reads_the_env_var()
    {
        var prev = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://broker:3128");
            Assert.Equal(new Uri("http://broker:3128"), GrpcProxyChannel.ResolveHttpsProxy());

            Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
            Environment.SetEnvironmentVariable("https_proxy", null);
            Assert.Null(GrpcProxyChannel.ResolveHttpsProxy());
        }
        finally { Environment.SetEnvironmentVariable("HTTPS_PROXY", prev); }
    }

    [Fact]
    public async Task TunnelAsync_sends_CONNECT_and_completes_on_a_2xx_reply()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var port = ((IPEndPoint)proxy.LocalEndpoint).Port;

        // Fake proxy: capture the CONNECT request block, then reply 200.
        var served = Task.Run(async () =>
        {
            using var conn = await proxy.AcceptTcpClientAsync(cts.Token);
            var s = conn.GetStream();
            var request = await ReadRequestBlockAsync(s, cts.Token);
            await s.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), cts.Token);
            await Task.Delay(50, cts.Token);
            return request;
        }, cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        await GrpcProxyChannel.TunnelAsync(client.GetStream(), "api.example.com", 443, cts.Token);

        var seen = await served;
        Assert.Contains("CONNECT api.example.com:443 HTTP/1.1", seen); // the exact tunnel target the runner asked for
    }

    [Fact]
    public async Task TunnelAsync_throws_when_the_proxy_refuses()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var port = ((IPEndPoint)proxy.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            using var conn = await proxy.AcceptTcpClientAsync(cts.Token);
            var s = conn.GetStream();
            await ReadRequestBlockAsync(s, cts.Token);
            await s.WriteAsync("HTTP/1.1 403 Forbidden\r\n\r\n"u8.ToArray(), cts.Token); // e.g. host not allowlisted
            await Task.Delay(50, cts.Token);
        }, cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        await Assert.ThrowsAsync<IOException>(
            () => GrpcProxyChannel.TunnelAsync(client.GetStream(), "blocked.example.com", 443, cts.Token));
    }

    private static async Task<string> ReadRequestBlockAsync(NetworkStream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await s.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0) break;
            sb.Append((char)one[0]);
        }
        return sb.ToString();
    }
}
