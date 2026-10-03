using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.Runner;
using Mintokei.Runner.Contracts.Tunnel;
using Xunit;

namespace Mintokei.Runner.Client.Tests;

public class TunnelProtocolTests
{
    [Fact]
    public void Legacy_frames_default_to_HTTP_and_new_frames_preserve_HTTPS()
    {
        var id = Guid.NewGuid();
        var http = new TunnelHttpRequest("GET", "/login", "?next=%2Fapi", 5000, new(), "https");
        var ws = new TunnelWsOpenRequest("/ws", "?token=test", 5000, null, new(), "https");

        Assert.Equal("https", TunnelFrameCodec.DecodeRequest(TunnelFrameCodec.EncodeRequest(id, http, [])).Request.Scheme);
        Assert.Equal("https", TunnelFrameCodec.DecodeWsOpen(TunnelFrameCodec.EncodeWsOpen(id, ws)).Request.Scheme);

        var legacyHttp = LegacyFrame(TunnelFrameType.HttpRequest, id,
            new { method = "GET", path = "/", queryString = (string?)null, port = 5000, headers = new Dictionary<string, string>() });
        var legacyWs = LegacyFrame(TunnelFrameType.WsOpen, id,
            new { path = "/ws", queryString = (string?)null, port = 5000, subProtocol = (string?)null, headers = new Dictionary<string, string>() });
        Assert.Equal("http", TunnelFrameCodec.DecodeRequest(legacyHttp).Request.Scheme);
        Assert.Equal("http", TunnelFrameCodec.DecodeWsOpen(legacyWs).Request.Scheme);
    }

    [Fact]
    public async Task Local_HTTPS_certificates_are_rejected_by_default_and_accepted_only_when_opted_in()
    {
        await using var servers = await LocalServers.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var strict = TunnelClient.CreateLocalHttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => strict.GetAsync(servers.HttpsUrl, cts.Token));

        using var optedIn = TunnelClient.CreateLocalHttpClient(allowInvalidLocalhostCertificates: true);
        using var response = await optedIn.GetAsync(servers.HttpsUrl, cts.Token);
        Assert.Equal("local gateway", await response.Content.ReadAsStringAsync(cts.Token));
    }

    [Theory]
    [InlineData("http", false, true)]
    [InlineData("https", false, false)]
    [InlineData("https", true, true)]
    public async Task HTTP_tunnel_uses_selected_transport_and_certificate_policy(string scheme, bool allowInvalid, bool succeeds)
    {
        await using var servers = await LocalServers.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var tunnel = await servers.ConnectTunnelAsync(cts.Token);
        using var writeLock = new SemaphoreSlim(1, 1);
        using var client = TunnelClient.CreateLocalHttpClient(allowInvalid);
        var port = scheme == "https" ? servers.HttpsUrl.Port : servers.HttpUrl.Port;
        var request = new TunnelHttpRequest("GET", "/?next=%2Fapi", null, port, new(), scheme);

        await TunnelRequestHandler.HandleAsync(client, tunnel, writeLock, Guid.NewGuid(), request, ReadOnlyMemory<byte>.Empty, cts.Token);
        var frame = await servers.Frames.Reader.ReadAsync(cts.Token);

        Assert.Equal(succeeds ? TunnelFrameType.HttpResponse : TunnelFrameType.Error, TunnelFrameCodec.DecodeHeader(frame).FrameType);
        if (succeeds)
        {
            var (_, response, body) = TunnelFrameCodec.DecodeResponse(frame);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("local gateway", Encoding.UTF8.GetString(body.Span));
            Assert.Contains("/?next=%2Fapi", response.Headers["X-Seen-Path"]);
        }
    }

    [Theory]
    [InlineData("http", false, true)]
    [InlineData("https", false, false)]
    [InlineData("https", true, true)]
    public async Task WebSocket_tunnel_uses_WS_or_WSS_and_relays_data(string scheme, bool allowInvalid, bool succeeds)
    {
        await using var servers = await LocalServers.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var tunnel = await servers.ConnectTunnelAsync(cts.Token);
        using var writeLock = new SemaphoreSlim(1, 1);
        var sessions = new RunnerWsSessionStore();
        var id = Guid.NewGuid();
        var port = scheme == "https" ? servers.HttpsUrl.Port : servers.HttpUrl.Port;
        var request = new TunnelWsOpenRequest("/ws", null, port, null, new(), scheme);

        var handling = TunnelWsHandler.HandleAsync(tunnel, writeLock, id, request, sessions, NullLogger.Instance, cts.Token, allowInvalid);
        var frame = await servers.Frames.Reader.ReadAsync(cts.Token);
        Assert.Equal(succeeds ? TunnelFrameType.WsOpened : TunnelFrameType.Error, TunnelFrameCodec.DecodeHeader(frame).FrameType);
        if (succeeds)
        {
            // The open frame is sent just before the runner registers its local session. Wait until
            // that registration is visible by retrying the enqueue, bounded by the test timeout.
            while (!sessions.TryEnqueueFromApi(id, new TunnelWsDataHeader(true), Encoding.UTF8.GetBytes("hello")))
                await Task.Delay(10, cts.Token);
            var echo = await servers.Frames.Reader.ReadAsync(cts.Token);
            var (_, header, body) = TunnelFrameCodec.DecodeWsData(echo);
            Assert.True(header.IsText);
            Assert.Equal("hello", Encoding.UTF8.GetString(body.Span));
            sessions.TryCloseFromApi(id, new TunnelWsCloseHeader(1000, "Done"));
        }
        await handling.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task Unsupported_schemes_fail_before_connecting_to_a_local_server()
    {
        await using var servers = await LocalServers.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var tunnel = await servers.ConnectTunnelAsync(cts.Token);
        using var writeLock = new SemaphoreSlim(1, 1);
        using var client = TunnelClient.CreateLocalHttpClient();
        var request = new TunnelHttpRequest("GET", "/", null, servers.HttpUrl.Port, new(), "file");

        await TunnelRequestHandler.HandleAsync(client, tunnel, writeLock, Guid.NewGuid(), request, ReadOnlyMemory<byte>.Empty, cts.Token);
        Assert.Contains("scheme must be http or https", TunnelFrameCodec.DecodeError(await servers.Frames.Reader.ReadAsync(cts.Token)).Error.Message);
    }

    private static byte[] LegacyFrame(TunnelFrameType type, Guid id, object header)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header);
        var frame = new byte[TunnelFrameCodec.FixedHeaderSize + json.Length];
        frame[0] = (byte)type;
        id.TryWriteBytes(frame.AsSpan(1, 16));
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(17, 4), json.Length);
        json.CopyTo(frame.AsSpan(TunnelFrameCodec.FixedHeaderSize));
        return frame;
    }

    /// <summary>Two real loopback listeners and a tunnel socket; HTTPS uses a fresh self-signed cert.</summary>
    private sealed class LocalServers(WebApplication app, X509Certificate2 certificate, Uri httpUrl, Uri httpsUrl, Channel<byte[]> frames) : IAsyncDisposable
    {
        public Uri HttpUrl => httpUrl;
        public Uri HttpsUrl => httpsUrl;
        public Channel<byte[]> Frames => frames;

        public static async Task<LocalServers> StartAsync()
        {
            using var key = RSA.Create(2048);
            var certRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            certRequest.CertificateExtensions.Add(names.Build());
            using var signed = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var certificate = X509CertificateLoader.LoadPkcs12(signed.Export(X509ContentType.Pfx), null);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
            });
            var app = builder.Build();
            app.UseWebSockets();
            var frames = Channel.CreateUnbounded<byte[]>();
            app.Map("/tunnel", async context =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                try
                {
                    var buffer = new byte[16 * 1024];
                    while (socket.State == WebSocketState.Open)
                    {
                        using var message = new MemoryStream();
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                            if (result.MessageType == WebSocketMessageType.Close) return;
                            message.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);
                        await frames.Writer.WriteAsync(message.ToArray(), context.RequestAborted);
                    }
                }
                catch (WebSocketException) { }
                catch (OperationCanceledException) { }
            });
            app.Map("/ws", async context =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[1024];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", context.RequestAborted);
                        return;
                    }
                    await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, context.RequestAborted);
                }
            });
            app.MapGet("/", async context =>
            {
                context.Response.Headers["X-Seen-Path"] = context.Request.Path + context.Request.QueryString;
                var body = Encoding.UTF8.GetBytes("local gateway");
                context.Response.ContentLength = body.Length;
                await context.Response.Body.WriteAsync(body);
            });
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(address => new Uri(address)).ToArray();
            return new LocalServers(app, certificate, addresses.Single(address => address.Scheme == "http"), addresses.Single(address => address.Scheme == "https"), frames);
        }

        public async Task<ClientWebSocket> ConnectTunnelAsync(CancellationToken ct)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new UriBuilder(httpUrl) { Scheme = "ws", Path = "/tunnel" }.Uri, ct);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            certificate.Dispose();
        }
    }
}
