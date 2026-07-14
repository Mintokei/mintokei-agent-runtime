using Grpc.Core;
using Microsoft.Extensions.Logging;
using Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner;

/// <summary>
/// Holds the live runner-side <c>OpenQuery</c> request-stream writer so that
/// query responses can flow back over the dedicated gRPC stream instead of
/// SignalR <c>ReportQueryResult</c>. Existing handlers call
/// <see cref="TrySendResultAsync"/> first, falling back to SignalR when no
/// stream is registered.
/// </summary>
public sealed class GrpcQueryStreamManager(ILogger<GrpcQueryStreamManager> logger)
{
    private readonly object _lock = new();
    private IClientStreamWriter<QueryClientMessage>? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool IsOpen
    {
        get { lock (_lock) return _writer is not null; }
    }

    public void RegisterWriter(IClientStreamWriter<QueryClientMessage> writer)
    {
        lock (_lock) _writer = writer;
        logger.LogDebug("GrpcQueryStreamManager: writer registered");
    }

    public void UnregisterWriter(IClientStreamWriter<QueryClientMessage> writer)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_writer, writer)) _writer = null;
        }
        logger.LogDebug("GrpcQueryStreamManager: writer unregistered");
    }

    /// <summary>
    /// Wraps the JSON response in a generic <c>BrowseFilesystemResult</c> oneof
    /// — the API server reads only <c>queryId</c> + <c>result_json</c> so the
    /// specific oneof variant is irrelevant on the wire. Picking one variant
    /// for all responses keeps the runner-side code simple and avoids each
    /// handler needing to know its proto type.
    /// </summary>
    public async Task<bool> TrySendResultAsync(string queryId, string resultJson, CancellationToken ct = default)
    {
        IClientStreamWriter<QueryClientMessage>? writer;
        lock (_lock) writer = _writer;
        if (writer is null) return false;

        var msg = new QueryClientMessage
        {
            QueryId = queryId,
            Browse = new BrowseFilesystemResult { ResultJson = resultJson },
        };

        await _writeLock.WaitAsync(ct);
        try
        {
            await writer.WriteAsync(msg);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenQuery send failed for queryId={QueryId}; caller will fall back to SignalR", queryId);
            lock (_lock)
            {
                if (ReferenceEquals(_writer, writer)) _writer = null;
            }
            return false;
        }
        finally
        {
            try { _writeLock.Release(); } catch (ObjectDisposedException) { }
        }
    }
}
