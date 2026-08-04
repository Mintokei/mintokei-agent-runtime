using System.Net.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts.Tunnel;

namespace Mintokei.Runner;

/// <summary>
/// Maintains a dedicated WebSocket tunnel to the API for HTTP and WebSocket proxying.
/// Connects independently of SignalR and reconnects with exponential backoff.
/// </summary>
public sealed class TunnelClient : BackgroundService
{
    private readonly RunnerOptions _options;
    private readonly TokenRefreshService _tokenRefreshService;
    private readonly ILogger<TunnelClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly RunnerWsSessionStore _wsSessions = new();

    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    public TunnelClient(
        IOptions<RunnerOptions> options,
        TokenRefreshService tokenRefreshService,
        ILogger<TunnelClient> logger)
    {
        _options = options.Value;
        _tokenRefreshService = tokenRefreshService;
        _logger = logger;

        _httpClient = new HttpClient(new HttpClientHandler
        {
            // Don't follow redirects — let the browser handle them
            AllowAutoRedirect = false,
        })
        {
            // No global timeout — long-lived SSE connections need to stay open.
            // The local server still controls request lifetime via cancellation.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reconnectAttempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync(stoppingToken);
                reconnectAttempt = 0; // Reset on clean disconnect
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _wsSessions.CloseAll();
                var delay = ReconnectDelays[Math.Min(reconnectAttempt, ReconnectDelays.Length - 1)];
                _logger.LogWarning(ex, "Tunnel connection failed, reconnecting in {Delay}...", delay);
                await Task.Delay(delay, stoppingToken);
                reconnectAttempt++;
            }
        }
    }

    private async Task ConnectAndRunAsync(CancellationToken ct)
    {
        var token = await _tokenRefreshService.GetCurrentTokenAsync();
        if (token is null)
        {
            _logger.LogWarning("No JWT token available for tunnel, waiting...");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return;
        }

        var baseUrl = _options.BackendUrl.TrimEnd('/');
        var wsScheme = baseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var httpStripped = baseUrl.Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                                   .Replace("http://", "", StringComparison.OrdinalIgnoreCase);
        var uri = new Uri($"{wsScheme}://{httpStripped}/ws/tunnel?access_token={token}");

        using var ws = new ClientWebSocket();
        TunnelClientLog.TunnelConnecting(_logger, wsScheme, httpStripped);
        await ws.ConnectAsync(uri, ct);
        _logger.LogInformation("Tunnel WebSocket connected");

        await ReadLoopAsync(ws, ct);
    }

    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        const int maxMessageSize = 10 * 1024 * 1024;
        var buffer = new byte[64 * 1024];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Tunnel WebSocket received close frame");
                    return;
                }

                ms.Write(buffer, 0, result.Count);

                if (ms.Length > maxMessageSize)
                {
                    _logger.LogWarning("Tunnel request frame exceeded max size, dropping");
                    while (!result.EndOfMessage)
                        result = await ws.ReceiveAsync(buffer, ct);
                    ms.SetLength(0);
                    break;
                }
            } while (!result.EndOfMessage);

            if (ms.Length == 0)
                continue;

            var frame = ms.ToArray();
            if (frame.Length < TunnelFrameCodec.FixedHeaderSize)
            {
                _logger.LogWarning("Tunnel frame too small ({Length} bytes)", frame.Length);
                continue;
            }

            // Handle the request on a background task so we don't block the read loop
            _ = HandleFrameAsync(ws, frame, ct);
        }
    }

    private async Task HandleFrameAsync(ClientWebSocket ws, byte[] frame, CancellationToken ct)
    {
        try
        {
            var (frameType, requestId, _) = TunnelFrameCodec.DecodeHeader(frame);

            switch (frameType)
            {
                case TunnelFrameType.HttpRequest:
                {
                    var (_, request, body) = TunnelFrameCodec.DecodeRequest(frame);
                    await TunnelRequestHandler.HandleAsync(_httpClient, ws, _writeLock, requestId, request, body, ct);
                    break;
                }
                case TunnelFrameType.Ping:
                {
                    var pongFrame = TunnelFrameCodec.EncodePong(requestId);
                    await SendFrameAsync(ws, pongFrame, ct);
                    break;
                }
                case TunnelFrameType.WsOpen:
                {
                    var (_, request) = TunnelFrameCodec.DecodeWsOpen(frame);
                    _ = TunnelWsHandler.HandleAsync(ws, _writeLock, requestId, request, _wsSessions, _logger, ct);
                    break;
                }
                case TunnelFrameType.WsData:
                {
                    var (_, header, body) = TunnelFrameCodec.DecodeWsData(frame);
                    _wsSessions.TryEnqueueFromApi(requestId, header, body.ToArray());
                    break;
                }
                case TunnelFrameType.WsClose:
                {
                    var (_, close) = TunnelFrameCodec.DecodeWsClose(frame);
                    _wsSessions.TryCloseFromApi(requestId, close);
                    break;
                }
                default:
                    _logger.LogWarning("Unexpected tunnel frame type {FrameType}", frameType);
                    break;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Error handling tunnel frame");
        }
    }

    private async Task SendFrameAsync(WebSocket ws, byte[] frame, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        _writeLock.Dispose();
        base.Dispose();
    }
}

internal static partial class TunnelClientLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Connecting tunnel WebSocket to {WsScheme}://{HttpStripped}/ws/tunnel...")]
    public static partial void TunnelConnecting(ILogger logger, string wsScheme, string httpStripped);
}
