using Mintokei.AgentEngine.AgentTools;

namespace Mintokei.AgentControlPlane;

/// <summary>
/// The capacity/admission surface a host uses to reason about runner slots: per-machine counts,
/// in-flight pending claims, the per-machine lock, and a slot snapshot for the local null-or-machine
/// fold, idle eviction, and logging.
/// </summary>
public interface ICapacityLedger
{
    /// <summary>Per-machine semaphore (1,1) held across an atomic cap-check + reserve.</summary>
    SemaphoreSlim GetMachineLock(Guid? machineId);

    /// <summary>Reserves an in-flight slot; dispose once the spawn settles (success or failure).</summary>
    IDisposable AddPendingClaim(Guid? machineId, Guid? agentId);

    int GetPendingClaimsByMachine(Guid? machineId);
    int GetPendingClaimsByMachineAndAgent(Guid? machineId, Guid agentId);

    /// <summary>Live (non-exited) sessions on a machine — includes idle (an idle CLI still holds a slot).</summary>
    int CountByMachine(Guid machineId);
    int CountByMachineAndAgent(Guid machineId, Guid agentId);
    /// <summary>Active (non-idle) live sessions on a machine.</summary>
    int CountActiveByMachine(Guid machineId);

    /// <summary>Snapshot of every registered slot, for the fold/eviction/logging paths the counts
    /// above can't express (local null-or-machine matching, picking an idle victim).</summary>
    IReadOnlyList<CapacitySlot> GetSlots();
}

/// <summary>A capacity snapshot of one registered session — its opaque registry <see cref="Key"/> (the
/// control plane never interprets it; Mintokei passes the <c>AgentTask.Id</c>), the backing session id,
/// where it runs, its agent and tool, whether it's idle, and whether its process has exited. A consumer
/// evicts a victim by <see cref="Key"/> (Mintokei resolves the workspace from the DB).</summary>
public sealed record CapacitySlot(
    Guid Key, Guid SessionId, Guid? RunnerMachineId, Guid? AgentId,
    AgentToolKey Tool, DateTimeOffset? IdleSince, bool HasExited);
