using Grpc.Core;
using Microsoft.Extensions.Logging;
using Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner;

/// <summary>
/// Holds the live runner-side <c>OpenWatcher</c> request-stream writer so
/// that <see cref="RunnerHostedService.OnFileSystemChangedAsync"/> can route
/// FileSystemChanged events through the dedicated gRPC stream instead of
/// the SignalR <c>ReportFileSystemChanged</c> hub call. <see cref="GrpcRunnerHostedService"/>
/// registers / unregisters around the stream lifetime.
/// </summary>
public sealed class GrpcWatcherStreamManager(ILogger<GrpcWatcherStreamManager> logger)
{
    private readonly object _lock = new();
    private IClientStreamWriter<WatcherClientMessage>? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool IsOpen
    {
        get { lock (_lock) return _writer is not null; }
    }

    public void RegisterWriter(IClientStreamWriter<WatcherClientMessage> writer)
    {
        lock (_lock) _writer = writer;
        logger.LogDebug("GrpcWatcherStreamManager: writer registered");
    }

    public void UnregisterWriter(IClientStreamWriter<WatcherClientMessage> writer)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_writer, writer)) _writer = null;
        }
        logger.LogDebug("GrpcWatcherStreamManager: writer unregistered");
    }

    public async Task<bool> TrySendAsync(WatcherClientMessage msg, CancellationToken ct = default)
    {
        IClientStreamWriter<WatcherClientMessage>? writer;
        lock (_lock) writer = _writer;
        if (writer is null) return false;

        await _writeLock.WaitAsync(ct);
        try
        {
            await writer.WriteAsync(msg);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenWatcher send failed; caller will fall back to SignalR");
            // Drop the stale writer so subsequent calls return false fast.
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
