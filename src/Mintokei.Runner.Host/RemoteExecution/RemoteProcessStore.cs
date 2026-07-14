using System.Collections.Concurrent;

namespace Mintokei.Runner.Host.RemoteExecution;

/// <summary>
/// In-memory store for active remote process handles, keyed by correlation ID.
/// The RunnerHub looks up handles here to route output from runners.
/// </summary>
public sealed class RemoteProcessStore
{
    private readonly ConcurrentDictionary<Guid, RemoteProcessHandle> _store = new();

    public void Add(Guid correlationId, RemoteProcessHandle handle) => _store[correlationId] = handle;

    public RemoteProcessHandle? Get(Guid correlationId) =>
        _store.TryGetValue(correlationId, out var handle) ? handle : null;

    public RemoteProcessHandle? Remove(Guid correlationId) =>
        _store.TryRemove(correlationId, out var handle) ? handle : null;

    public IReadOnlyCollection<Guid> GetAllCorrelationIds() => _store.Keys.ToList();

    /// <summary>
    /// Marks all handles for a given machine as <em>disconnected</em> (transport
    /// gone) — NOT exited. Called when a runner disconnects: it unblocks each
    /// handle's output stream and drops it from the store so the execution
    /// services stop routing to a dead stream, but it does not claim the process
    /// died. The process may still be alive/warm on the runner and resumable once
    /// it reconnects; only a runner-reported ProcessCompleted sets HasExited.
    /// </summary>
    public void SetAllDisconnectedForMachine(Guid machineId)
    {
        foreach (var (correlationId, handle) in _store)
        {
            if (handle.MachineId == machineId)
            {
                handle.MarkDisconnected();
                _store.TryRemove(correlationId, out _);
            }
        }
    }
}
