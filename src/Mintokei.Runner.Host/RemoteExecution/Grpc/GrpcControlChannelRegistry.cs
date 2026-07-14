using System.Collections.Concurrent;
using global::Grpc.Core;
using Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner.Host.RemoteExecution.Grpc;

/// <summary>
/// Per-machine registry of open gRPC <c>Control</c> response-stream writers.
/// The control stream is the only server→runner channel that exists for the
/// machine itself (as opposed to per-correlation OpenTask, etc.), so this is
/// where bootstrap signals live: most importantly the <c>OpenTaskRequest</c>
/// the API sends to ask the runner to open an OpenTask stream for a new
/// correlation.
///
/// Registered once per Control stream lifetime by <c>RunnerLinkService.Control</c>.
/// </summary>
public sealed class GrpcControlChannelRegistry
{
    private readonly ConcurrentDictionary<Guid, Channel> _channels = new();

    private sealed class Channel
    {
        public required IServerStreamWriter<ServerControlMessage> Writer { get; init; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    public void Register(Guid machineId, IServerStreamWriter<ServerControlMessage> writer)
        => _channels[machineId] = new Channel { Writer = writer };

    public void Unregister(Guid machineId, IServerStreamWriter<ServerControlMessage> writer)
    {
        if (_channels.TryGetValue(machineId, out var existing) && ReferenceEquals(existing.Writer, writer))
            _channels.TryRemove(machineId, out _);
    }

    public bool IsOpen(Guid machineId) => _channels.ContainsKey(machineId);

    /// <summary>
    /// Ask the runner to open an OpenTask stream for <paramref name="correlationId"/>.
    /// Returns false if no Control stream is registered for the machine
    /// (caller should leave the message Pending and retry on the next sweep).
    /// </summary>
    public async Task<bool> TryRequestTaskStreamOpenAsync(
        Guid machineId, Guid correlationId, CancellationToken ct)
    {
        if (!_channels.TryGetValue(machineId, out var channel)) return false;

        await channel.WriteLock.WaitAsync(ct);
        try
        {
            await channel.Writer.WriteAsync(new ServerControlMessage
            {
                OpenTask = new OpenTaskRequest
                {
                    TaskCorrelationId = correlationId.ToString(),
                },
            }, ct);
            return true;
        }
        finally
        {
            channel.WriteLock.Release();
        }
    }
}
