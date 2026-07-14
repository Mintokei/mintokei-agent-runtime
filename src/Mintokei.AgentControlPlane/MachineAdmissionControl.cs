using System.Collections.Concurrent;
using System.Linq;

namespace Mintokei.AgentControlPlane;

/// <summary>
/// The concurrency-critical admission bookkeeping for capacity enforcement, independent of any
/// session or context type:
/// <list type="bullet">
/// <item><b>Pending claims</b> — slots reserved by a cap-check that has passed but whose session
/// isn't registered yet (the spawn is in flight over the network). Concurrent admissions must add
/// these to the live count so they can't both pass on the same stale count and overshoot the cap.</item>
/// <item><b>Per-machine lock</b> — a semaphore taken to make a cap-check + reserve atomic for one
/// machine. Held only across the bookkeeping (microseconds), never across the network spawn.</item>
/// </list>
///
/// Extracted verbatim (behaviour-identical) from <see cref="InMemoryAgentProcessStore"/> so the
/// DB-free <see cref="DefaultAgentControlPlane"/> can own capacity without depending on the legacy process store.
/// Stage C deletes the store's copy and points <c>AgentInstanceLimitEnforcer</c> here.
/// </summary>
internal sealed class MachineAdmissionControl
{
    // Sentinel key for "no machine id" (local fallback) — mirrors the store.
    private static readonly Guid NoMachineKey = Guid.Empty;

    private readonly ConcurrentDictionary<Guid, PendingClaim> _pendingClaims = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _machineLocks = new();

    private sealed record PendingClaim(Guid? MachineId, Guid? AgentId);

    /// <summary>In-flight reservations on a machine (null = local).</summary>
    public int GetPendingClaimsByMachine(Guid? machineId)
    {
        var key = machineId ?? NoMachineKey;
        return _pendingClaims.Values.Count(c => (c.MachineId ?? NoMachineKey) == key);
    }

    /// <summary>In-flight reservations on a machine for one agent.</summary>
    public int GetPendingClaimsByMachineAndAgent(Guid? machineId, Guid agentId)
    {
        var key = machineId ?? NoMachineKey;
        return _pendingClaims.Values.Count(c =>
            (c.MachineId ?? NoMachineKey) == key && c.AgentId == agentId);
    }

    /// <summary>Reserves a pending slot. Dispose the returned handle once the spawn settles —
    /// success (the session is now registered and counts there) or failure (gives the slot back).</summary>
    public IDisposable AddPendingClaim(Guid? machineId, Guid? agentId)
    {
        var claimId = Guid.NewGuid();
        _pendingClaims[claimId] = new PendingClaim(machineId, agentId);
        return new Release(this, claimId);
    }

    /// <summary>The per-machine semaphore (1,1). The enforcer holds it across cap-check + reserve so
    /// concurrent admissions can't both pass with the same stale count.</summary>
    public SemaphoreSlim GetMachineLock(Guid? machineId)
        => _machineLocks.GetOrAdd(machineId ?? NoMachineKey, _ => new SemaphoreSlim(1, 1));

    private sealed class Release : IDisposable
    {
        private readonly MachineAdmissionControl _owner;
        private readonly Guid _claimId;
        private int _disposed;

        public Release(MachineAdmissionControl owner, Guid claimId)
        {
            _owner = owner;
            _claimId = claimId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner._pendingClaims.TryRemove(_claimId, out _);
        }
    }
}
