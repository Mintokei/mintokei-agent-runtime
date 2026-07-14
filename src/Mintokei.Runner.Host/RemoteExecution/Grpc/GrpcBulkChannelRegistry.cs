using System.Collections.Concurrent;
using global::Grpc.Core;
using Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner.Host.RemoteExecution.Grpc;

/// <summary>
/// Tracks open per-machine gRPC OpenBulk streams. Singleton.
///
/// RemoteFilesystemService consults this registry for large-payload reads
/// (GetFileContent, GetImageFile) before falling back to SignalR. With
/// these reads on a dedicated stream they cannot head-of-line block the
/// small/medium Query lane or the per-task Task streams.
/// </summary>
public sealed class GrpcBulkChannelRegistry
{
    private readonly ConcurrentDictionary<Guid, Channel> _channels = new();

    private sealed class Channel
    {
        public required IServerStreamWriter<BulkServerMessage> Writer { get; init; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    public void Register(Guid machineId, IServerStreamWriter<BulkServerMessage> writer)
        => _channels[machineId] = new Channel { Writer = writer };

    public void Unregister(Guid machineId, IServerStreamWriter<BulkServerMessage> writer)
    {
        if (_channels.TryGetValue(machineId, out var existing) && ReferenceEquals(existing.Writer, writer))
            _channels.TryRemove(machineId, out _);
    }

    public bool IsOpen(Guid machineId) => _channels.ContainsKey(machineId);

    public async Task<bool> TrySendAsync(Guid machineId, BulkServerMessage msg, CancellationToken ct)
    {
        if (!_channels.TryGetValue(machineId, out var channel)) return false;

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
