using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner;

/// <summary>
/// Holds the live runner-side <c>OpenBulk</c> request-stream writer for
/// large-payload responses (file content / image reads). Today the runner
/// sends the existing JSON-encoded response as a single chunk with
/// <c>last = true</c> — chunked sending of multi-MB payloads is a future
/// optimization the wire format already supports.
/// </summary>
public sealed class GrpcBulkStreamManager(ILogger<GrpcBulkStreamManager> logger)
{
    private readonly object _lock = new();
    private IClientStreamWriter<BulkClientMessage>? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool IsOpen
    {
        get { lock (_lock) return _writer is not null; }
    }

    public void RegisterWriter(IClientStreamWriter<BulkClientMessage> writer)
    {
        lock (_lock) _writer = writer;
        logger.LogDebug("GrpcBulkStreamManager: writer registered");
    }

    public void UnregisterWriter(IClientStreamWriter<BulkClientMessage> writer)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_writer, writer)) _writer = null;
        }
        logger.LogDebug("GrpcBulkStreamManager: writer unregistered");
    }

    /// <summary>
    /// Send the response as a single chunk with <c>last = true</c>.
    /// </summary>
    public async Task<bool> TrySendSingleChunkAsync(
        string queryId,
        string mimeType,
        byte[] data,
        CancellationToken ct = default)
    {
        IClientStreamWriter<BulkClientMessage>? writer;
        lock (_lock) writer = _writer;
        if (writer is null) return false;

        var msg = new BulkClientMessage
        {
            QueryId = queryId,
            Chunk = new BulkChunk
            {
                ChunkIndex = 0,
                Last = true,
                MimeType = mimeType,
                Data = ByteString.CopyFrom(data),
            },
        };

        await _writeLock.WaitAsync(ct);
        try
        {
            await writer.WriteAsync(msg);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenBulk send failed for queryId={QueryId}; caller will fall back to SignalR", queryId);
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
