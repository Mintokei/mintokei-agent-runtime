using Mintokei.Runner.Host.RemoteExecution;

namespace RemoteRunnerMinimal;

/// <summary>
/// The one seam a minimal host still supplies. <see cref="IRemoteProcessRecovery"/> re-derives a
/// remote process handle for a correlation whose in-memory handle was lost across a host restart —
/// which only matters if you persist tasks and survive restarts. This sample keeps nothing across a
/// restart, so recovery is a no-op: it always returns null (correlation cannot be recovered), and the
/// affected session simply ends.
/// </summary>
public sealed class NoOpRemoteProcessRecovery : IRemoteProcessRecovery
{
    public Task<RemoteProcessHandle?> TryRecoverAsync(Guid correlationId, Guid machineId) =>
        Task.FromResult<RemoteProcessHandle?>(null);
}
