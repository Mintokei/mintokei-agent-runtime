using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Mintokei.Runner.Contracts.Tunnel;

namespace Mintokei.Runner;

/// <summary>
/// Handles a WebSocket tunnel session on the runner side.
/// Connects to a local WebSocket server and relays data bidirectionally through the tunnel.
/// </summary>
public static class TunnelWsHandler
{
    public static async Task HandleAsync(
        WebSocket tunnelWs,
        SemaphoreSlim writeLock,
        Guid sessionId,
        TunnelWsOpenRequest request,
        RunnerWsSessionStore sessionStore,
        ILogger logger,
        CancellationToken ct)
    {
        ClientWebSocket? localWs = null;
        RunnerWsSession? session = null;
        try
        {
            // 1. Connect to local WebSocket server
            localWs = new ClientWebSocket();
            if (request.SubProtocol is not null)
                localWs.Options.AddSubProtocol(request.SubProtocol);

            var url = $"ws://localhost:{request.Port}{request.Path}{request.QueryString}";
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(connectTimeout.Token, ct);
            await localWs.ConnectAsync(new Uri(url), linked.Token);

            // 2. Send WsOpened confirmation
            var openedFrame = TunnelFrameCodec.EncodeWsOpened(sessionId,
                new TunnelWsOpenedResponse(localWs.SubProtocol));
            await SendFrameAsync(tunnelWs, writeLock, openedFrame, ct);

            // 3. Register session
            session = sessionStore.Create(sessionId, localWs, ct);

            // 4. Start bidirectional relay
            var localToTunnel = RelayLocalToTunnelAsync(sessionId, session, tunnelWs, writeLock, logger);
            var tunnelToLocal = RelayTunnelToLocalAsync(session, logger);

            await Task.WhenAny(localToTunnel, tunnelToLocal);

            // 5. Cleanup
            session.Cts.Cancel();
            sessionStore.TryRemove(sessionId);

            // Send WsClose to API if local server disconnected first
            if (localToTunnel.IsCompleted && tunnelWs.State == WebSocketState.Open)
            {
                var closeFrame = TunnelFrameCodec.EncodeWsClose(sessionId,
                    new TunnelWsCloseHeader((int)WebSocketCloseStatus.NormalClosure, "Local server disconnected"));
                try { await SendFrameAsync(tunnelWs, writeLock, closeFrame, CancellationToken.None); }
                catch { /* best effort */ }
            }

            // Close local WS gracefully
            if (localWs.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await localWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None); }
                catch { /* best effort */ }
            }

            session.Dispose();
            session = null;
            localWs = null; // Disposed by session
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "WS tunnel session {SessionId} failed to connect to localhost:{Port}", sessionId, request.Port);

            // Send error back to API so the browser WS gets closed
            var errorFrame = TunnelFrameCodec.EncodeError(sessionId,
                new TunnelErrorResponse($"WebSocket connection failed: {ex.Message}"));
            try { await SendFrameAsync(tunnelWs, writeLock, errorFrame, CancellationToken.None); }
            catch { /* best effort */ }
        }
        finally
        {
            if (session is not null)
            {
                sessionStore.TryRemove(sessionId);
                session.Dispose();
            }
            else
            {
                localWs?.Dispose();
            }
        }
    }

    private static async Task RelayLocalToTunnelAsync(
        Guid sessionId,
        RunnerWsSession session,
        WebSocket tunnelWs,
        SemaphoreSlim writeLock,
        ILogger logger)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (session.LocalWebSocket.State == WebSocketState.Open && !session.Cts.Token.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await session.LocalWebSocket.ReceiveAsync(buffer, session.Cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var isText = result.MessageType == WebSocketMessageType.Text;
                var dataFrame = TunnelFrameCodec.EncodeWsData(
                    sessionId, new TunnelWsDataHeader(isText), ms.ToArray());
                await SendFrameAsync(tunnelWs, writeLock, dataFrame, session.Cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local-to-tunnel relay error for session {SessionId}", sessionId);
        }
    }

    private static async Task RelayTunnelToLocalAsync(
        RunnerWsSession session,
        ILogger logger)
    {
        try
        {
            await foreach (var (header, body) in session.ApiToLocalQueue.Reader.ReadAllAsync(session.Cts.Token))
            {
                var msgType = header.IsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
                await session.LocalWebSocket.SendAsync(body, msgType, true, session.Cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tunnel-to-local relay error");
        }
    }

    private static async Task SendFrameAsync(WebSocket ws, SemaphoreSlim writeLock, byte[] frame, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
