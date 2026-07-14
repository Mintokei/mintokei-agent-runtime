using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Mintokei.Runner.Contracts.Tunnel;

namespace Mintokei.Runner;

/// <summary>
/// Tracks active local WebSocket connections on the runner side.
/// Each session corresponds to a browser WebSocket tunneled through the API.
/// </summary>
public sealed class RunnerWsSessionStore
{
    private readonly ConcurrentDictionary<Guid, RunnerWsSession> _sessions = new();

    public RunnerWsSession Create(Guid sessionId, ClientWebSocket localWs, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queue = Channel.CreateBounded<(TunnelWsDataHeader Header, byte[] Body)>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        var session = new RunnerWsSession(localWs, cts, queue);
        _sessions[sessionId] = session;
        return session;
    }

    public bool TryEnqueueFromApi(Guid sessionId, TunnelWsDataHeader header, byte[] body)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            return session.ApiToLocalQueue.Writer.TryWrite((header, body));
        return false;
    }

    public bool TryCloseFromApi(Guid sessionId, TunnelWsCloseHeader close)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.ApiToLocalQueue.Writer.TryComplete();
            session.Cts.Cancel();
            return true;
        }
        return false;
    }

    public bool TryRemove(Guid sessionId)
    {
        return _sessions.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Close all active sessions (e.g. when the tunnel reconnects).
    /// </summary>
    public void CloseAll()
    {
        foreach (var kvp in _sessions)
        {
            if (_sessions.TryRemove(kvp.Key, out var session))
            {
                session.ApiToLocalQueue.Writer.TryComplete();
                session.Cts.Cancel();
            }
        }
    }
}

/// <summary>
/// Represents an active local WebSocket connection on the runner side.
/// </summary>
public sealed class RunnerWsSession : IDisposable
{
    public ClientWebSocket LocalWebSocket { get; }
    public CancellationTokenSource Cts { get; }
    public Channel<(TunnelWsDataHeader Header, byte[] Body)> ApiToLocalQueue { get; }

    public RunnerWsSession(
        ClientWebSocket localWebSocket,
        CancellationTokenSource cts,
        Channel<(TunnelWsDataHeader Header, byte[] Body)> apiToLocalQueue)
    {
        LocalWebSocket = localWebSocket;
        Cts = cts;
        ApiToLocalQueue = apiToLocalQueue;
    }

    public void Dispose()
    {
        Cts.Dispose();
        LocalWebSocket.Dispose();
    }
}
