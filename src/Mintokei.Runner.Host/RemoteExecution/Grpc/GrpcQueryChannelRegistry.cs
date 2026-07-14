using System.Collections.Concurrent;
using global::Grpc.Core;
using Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner.Host.RemoteExecution.Grpc;

/// <summary>
/// Tracks open per-machine gRPC OpenQuery streams. Singleton.
///
/// RemoteFilesystemService consults this registry before falling back to
/// its slower compatibility path. With small/medium
/// FS RPC on a dedicated stream, file content / image reads on OpenBulk
/// can never head-of-line block them.
/// </summary>
public sealed class GrpcQueryChannelRegistry
{
    private readonly ConcurrentDictionary<Guid, Channel> _channels = new();

    private sealed class Channel
    {
        public required IServerStreamWriter<QueryServerMessage> Writer { get; init; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    public void Register(Guid machineId, IServerStreamWriter<QueryServerMessage> writer)
        => _channels[machineId] = new Channel { Writer = writer };

    public void Unregister(Guid machineId, IServerStreamWriter<QueryServerMessage> writer)
    {
        if (_channels.TryGetValue(machineId, out var existing) && ReferenceEquals(existing.Writer, writer))
            _channels.TryRemove(machineId, out _);
    }

    public bool IsOpen(Guid machineId) => _channels.ContainsKey(machineId);

    public async Task<bool> TrySendAsync(Guid machineId, QueryServerMessage msg, CancellationToken ct)
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
