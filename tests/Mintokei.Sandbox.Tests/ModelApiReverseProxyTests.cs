using System.Net;
using System.Net.Sockets;
using Mintokei.Sandbox.Broker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class ModelApiReverseProxyTests
{
    [Fact]
    public void ParseHeaders_reads_colon_and_equals_forms()
    {
        var h = ModelApiReverseProxy.ParseHeaders("x-api-key=sk-ant-abc\nAuthorization: Bearer sk-xyz");
        Assert.Equal(new KeyValuePair<string, string>("x-api-key", "sk-ant-abc"), h[0]);
        Assert.Equal(new KeyValuePair<string, string>("Authorization", "Bearer sk-xyz"), h[1]); // value's space kept, ':' split first
    }

    [Fact]
    public void BuildUpstreamRequest_rewrites_url_injects_auth_and_drops_hop_by_hop()
    {
        var proxy = new ModelApiReverseProxy("https://api.anthropic.com", [new("x-api-key", "sk-secret")]);
        using var req = proxy.BuildUpstreamRequest(
            "POST", "/v1/messages",
            [
                new("Host", "broker:3130"),                       // dropped (hop-by-hop-ish / re-set by HttpClient)
                new("anthropic-version", "2023-06-01"),           // preserved
                new("x-api-key", "sk-CLIENT-SHOULD-NOT-WIN"),      // overridden by the injected key
                new("Content-Type", "application/json"),          // → content header
            ],
            new MemoryStream("{}"u8.ToArray()));

        Assert.Equal("https://api.anthropic.com/v1/messages", req.RequestUri!.ToString());
        Assert.Equal("sk-secret", Assert.Single(req.Headers.GetValues("x-api-key")));   // injected key wins
        Assert.Equal("2023-06-01", Assert.Single(req.Headers.GetValues("anthropic-version")));
        Assert.False(req.Headers.Contains("Host"));
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Reverse_proxy_injects_the_key_upstream_without_the_client_ever_sending_it()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Fake upstream: records the auth header it saw, returns a canned body.
        string? upstreamSawKey = null;
        var upstreamPort = FreePort();
        using var upstream = new HttpListener();
        upstream.Prefixes.Add($"http://127.0.0.1:{upstreamPort}/");
        upstream.Start();
        var serveUpstream = Task.Run(async () =>
        {
            var ctx = await upstream.GetContextAsync();
            upstreamSawKey = ctx.Request.Headers["x-api-key"];
            var body = "pong"u8.ToArray();
            ctx.Response.OutputStream.Write(body);
            ctx.Response.Close();
        }, cts.Token);

        using var proxy = new ModelApiReverseProxy($"http://127.0.0.1:{upstreamPort}", [new("x-api-key", "sk-INJECTED")]);
        var proxyPort = FreePort();
        _ = proxy.RunAsync(proxyPort, cts.Token);
        await proxy.BoundPort;

        // Client (the "sandbox") talks plaintext to the broker and sends NO key.
        using var client = new HttpClient();
        var resp = await client.PostAsync($"http://127.0.0.1:{proxyPort}/v1/messages",
            new StringContent("{}"), cts.Token);
        var text = await resp.Content.ReadAsStringAsync(cts.Token);
        await serveUpstream;

        Assert.Equal("pong", text);                 // response streamed back to the client
        Assert.Equal("sk-INJECTED", upstreamSawKey); // upstream received the injected key…
        // …and the client never had it: the request it built carried no x-api-key of its own (asserted in BuildUpstreamRequest).

        await cts.CancelAsync();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
