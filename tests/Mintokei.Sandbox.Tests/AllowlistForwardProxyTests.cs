using System.Net;
using System.Net.Sockets;
using System.Text;
using Mintokei.Sandbox.Broker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class AllowlistForwardProxyTests
{
    [Theory]
    [InlineData(new[] { "github.com" }, "github.com", true)]
    [InlineData(new[] { "github.com" }, "api.github.com", false)]           // exact does NOT match subdomains
    [InlineData(new[] { "github.com" }, "evil.com", false)]
    [InlineData(new[] { "GitHub.com" }, "github.com", true)]                // case-insensitive
    [InlineData(new[] { ".githubusercontent.com" }, "raw.githubusercontent.com", true)]  // suffix matches subdomain
    [InlineData(new[] { ".githubusercontent.com" }, "githubusercontent.com", true)]      // …and the bare apex
    [InlineData(new[] { ".githubusercontent.com" }, "notgithubusercontent.com", false)]  // not a real suffix boundary
    [InlineData(new string[0], "github.com", false)]                       // empty allowlist denies all
    public void IsAllowed_matches_exact_and_suffix(string[] allow, string host, bool expected)
        => Assert.Equal(expected, AllowlistForwardProxy.IsAllowed(allow, host));

    [Fact]
    public async Task Tunnels_allowlisted_host_and_blocks_others()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // A hermetic upstream (no internet): the proxy CONNECTs here for the allowed case and echoes bytes back.
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var echo = EchoOnceAsync(upstream, cts.Token);

        var proxy = new AllowlistForwardProxy(["localhost"]);   // allow only "localhost"
        _ = proxy.RunAsync(0, cts.Token);
        var proxyPort = await proxy.BoundPort;

        // Allowed: CONNECT localhost:<upstream> → 200, and the tunnel round-trips bytes.
        var (allowedStatus, tunnel) = await ConnectAsync(proxyPort, $"localhost:{upstreamPort}", cts.Token);
        using (tunnel)
        {
            Assert.Contains("200", allowedStatus);
            await tunnel.WriteAsync("ping"u8.ToArray(), cts.Token);
            var buf = new byte[4];
            await tunnel.ReadExactlyAsync(buf, cts.Token);
            Assert.Equal("ping", Encoding.ASCII.GetString(buf));
        }
        await echo;

        // Denied: a host that isn't allowlisted → 403, no upstream connection attempted.
        var (deniedStatus, denied) = await ConnectAsync(proxyPort, "blocked.test:443", cts.Token);
        denied.Dispose();
        Assert.Contains("403", deniedStatus);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task Real_tls_egress_reaches_allowlisted_host_and_blocks_others()
    {
        if (Environment.GetEnvironmentVariable("MINTOKEI_SANDBOX_NET_ITEST") != "1")
            Assert.Skip("opt-in only: set MINTOKEI_SANDBOX_NET_ITEST=1 (needs internet) to run the real-TLS test");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var proxy = new AllowlistForwardProxy(["example.com"]); // allow only example.com
        _ = proxy.RunAsync(0, cts.Token);
        var port = await proxy.BoundPort;

        using var http = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
        });

        // Allowed host: real end-to-end TLS through the CONNECT tunnel succeeds (no MITM — the proxy never sees plaintext).
        using var ok = await http.GetAsync("https://example.com/", cts.Token);
        Assert.True(ok.IsSuccessStatusCode);

        // Denied host: the proxy answers CONNECT with 403, so the request fails — egress is genuinely bounded.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("https://www.google.com/", cts.Token));

        await cts.CancelAsync();
    }

    private static async Task EchoOnceAsync(TcpListener listener, CancellationToken ct)
    {
        using var c = await listener.AcceptTcpClientAsync(ct);
        var s = c.GetStream();
        var buf = new byte[64];
        var n = await s.ReadAsync(buf, ct);
        if (n > 0) await s.WriteAsync(buf.AsMemory(0, n), ct);
        await Task.Delay(100, ct); // let the client read before the socket closes
    }

    private static async Task<(string Status, NetworkStream Tunnel)> ConnectAsync(int proxyPort, string target, CancellationToken ct)
    {
        var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, proxyPort, ct);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n"), ct);
        var status = await ReadResponseHeadAsync(s, ct);
        return (status, s);
    }

    // Reads the response status line, then drains the header block to the blank line so the tunnel is clean.
    private static async Task<string> ReadResponseHeadAsync(NetworkStream s, CancellationToken ct)
    {
        var first = await ReadLineAsync(s, ct);
        string h;
        do { h = await ReadLineAsync(s, ct); } while (h.Length > 0);
        return first;
    }

    private static async Task<string> ReadLineAsync(NetworkStream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0 || one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') sb.Append((char)one[0]);
        }
        return sb.ToString();
    }
}
