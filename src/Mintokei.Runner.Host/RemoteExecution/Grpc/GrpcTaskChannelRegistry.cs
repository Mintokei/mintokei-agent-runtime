using System.Collections.Concurrent;
using global::Grpc.Core;
using Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner.Host.RemoteExecution.Grpc;

/// <summary>
/// Tracks open per-correlation gRPC Task streams (<see cref="RunnerLink.RunnerLinkBase.OpenTask"/>)
/// keyed by <c>(machineId, correlationId)</c>. Lets the rest of the API send a
/// <see cref="ServerTaskCommand"/> directly down a runner's stream when one is
/// open, bypassing the SignalR outbox path entirely.
///
/// Registered as a singleton. The OpenTask handler registers a stream as soon
/// as the handshake completes and unregisters in its <c>finally</c> block when
/// the stream closes (whether normally or by client disconnect / cancellation).
///
/// This commit only populates and cleans up the registry — no production
/// caller sends through it yet. The actual routing (OutboxProcessorService
/// + runner-side opener) lands in a follow-up so the cutover can be staged
/// behind the existing <c>Runner:EnableGrpc</c> flag.
/// </summary>
public sealed class GrpcTaskChannelRegistry
{
    private readonly ConcurrentDictionary<(Guid MachineId, Guid CorrelationId), Channel> _channels = new();

    private sealed class Channel
    {
        public required IServerStreamWriter<TaskServerMessage> Writer { get; init; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    /// <summary>
    /// Replaces any existing entry for the same key (a new stream from a
    /// reconnecting runner supersedes the stale one — the old one's
    /// <c>finally</c> will only deregister itself if it's still the entry).
    /// </summary>
    public void Register(Guid machineId, Guid correlationId, IServerStreamWriter<TaskServerMessage> writer)
    {
        _channels[(machineId, correlationId)] = new Channel { Writer = writer };
    }

    /// <summary>
    /// Removes the entry only if it still references the same writer. Safe
    /// to call from a stale stream's <c>finally</c> after a reconnect already
    /// replaced the entry.
    /// </summary>
    public void Unregister(Guid machineId, Guid correlationId, IServerStreamWriter<TaskServerMessage> writer)
    {
        var key = (machineId, correlationId);
        if (_channels.TryGetValue(key, out var existing) && ReferenceEquals(existing.Writer, writer))
            _channels.TryRemove(key, out _);
    }

    public bool IsOpen(Guid machineId, Guid correlationId)
        => _channels.ContainsKey((machineId, correlationId));

    /// <summary>
    /// Sends a <see cref="ServerTaskCommand"/> on the open stream, if any.
    /// Returns false when no stream is registered for that correlation; the
    /// caller should then fall back to the SignalR outbox path.
    /// gRPC stream writers are not threadsafe, so writes are serialized
    /// per-channel via an internal lock.
    /// </summary>
    public async Task<bool> TrySendAsync(
        Guid machineId,
        Guid correlationId,
        ServerTaskCommand command,
        CancellationToken ct)
    {
        if (!_channels.TryGetValue((machineId, correlationId), out var channel))
            return false;

        await channel.WriteLock.WaitAsync(ct);
        try
        {
            await channel.Writer.WriteAsync(new TaskServerMessage { Command = command }, ct);
            return true;
        }
        finally
        {
            channel.WriteLock.Release();
        }
    }
}
