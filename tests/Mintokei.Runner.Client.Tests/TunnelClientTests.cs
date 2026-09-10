using System.Net;
using System.Net.Sockets;
using System.Text;
using Mintokei.Runner;
using Xunit;

namespace Mintokei.Runner.Client.Tests;

public class TunnelClientTests
{
    [Fact]
    public async Task LocalHttpClient_never_replays_cookies_from_an_earlier_response()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var app = new TcpListener(IPAddress.Loopback, 0);
        app.Start();
        var port = ((IPEndPoint)app.LocalEndpoint).Port;

        // Fake runner-local app: signs the first caller in, then records what later requests carry.
        var served = Task.Run(async () =>
        {
            var requests = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                using var conn = await app.AcceptTcpClientAsync(cts.Token);
                var s = conn.GetStream();
                requests.Add(await ReadRequestBlockAsync(s, cts.Token));
                var cookie = i == 0 ? "Set-Cookie: session=alice; Path=/\r\n" : "";
                await s.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\n{cookie}Content-Length: 0\r\nConnection: close\r\n\r\n"), cts.Token);
            }
            return requests;
        }, cts.Token);

        using var client = TunnelClient.CreateLocalHttpClient();
        var url = $"http://127.0.0.1:{port}/";

        using var aliceSignsIn = await client.GetAsync(url, cts.Token);
        // The Set-Cookie still reaches the caller, so the tunnel can hand it back to the browser.
        Assert.Contains("session=alice; Path=/", aliceSignsIn.Headers.GetValues("Set-Cookie"));

        using var anotherUser = await client.GetAsync(url, cts.Token);

        using var bobRequest = new HttpRequestMessage(HttpMethod.Get, url);
        bobRequest.Headers.TryAddWithoutValidation("Cookie", "session=bob");
        using var bob = await client.SendAsync(bobRequest, cts.Token);

        var seen = await served;
        Assert.DoesNotContain("Cookie:", seen[1], StringComparison.OrdinalIgnoreCase); // nobody inherits alice's session
        Assert.Contains("Cookie: session=bob", seen[2], StringComparison.OrdinalIgnoreCase); // the browser's own cookies pass as-is
        Assert.DoesNotContain("alice", seen[2]);
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
