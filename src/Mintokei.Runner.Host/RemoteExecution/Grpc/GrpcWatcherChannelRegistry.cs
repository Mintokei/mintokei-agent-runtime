using System.Collections.Concurrent;
using global::Grpc.Core;
using Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner.Host.RemoteExecution.Grpc;

/// <summary>
/// Tracks open per-machine gRPC OpenWatcher streams. Singleton.
///
/// FileSystemWatcherService consults this registry before falling back to
/// its compatibility path for watcher commands. With watcher events on a dedicated physical stream, a flood on
/// the bulk/query channels can never head-of-line block fs notifications.
/// </summary>
public sealed class GrpcWatcherChannelRegistry
{
    private readonly ConcurrentDictionary<Guid, Channel> _channels = new();

    private sealed class Channel
    {
        public required IServerStreamWriter<WatcherServerMessage> Writer { get; init; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    public void Register(Guid machineId, IServerStreamWriter<WatcherServerMessage> writer)
    {
        _channels[machineId] = new Channel { Writer = writer };
    }

    public void Unregister(Guid machineId, IServerStreamWriter<WatcherServerMessage> writer)
    {
        if (_channels.TryGetValue(machineId, out var existing) && ReferenceEquals(existing.Writer, writer))
            _channels.TryRemove(machineId, out _);
    }

    public bool IsOpen(Guid machineId) => _channels.ContainsKey(machineId);

    public async Task<bool> TrySendAsync(Guid machineId, WatcherServerMessage msg, CancellationToken ct)
    {
        if (!_channels.TryGetValue(machineId, out var channel))
            return false;

        await channel.WriteLock.WaitAsync(ct);
        try
        {
            await channel.Writer.WriteAsync(msg, ct);
            return true;
        }
        finally
        {
            channel.WriteLock.Release();
        }
    }
}
