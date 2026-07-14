namespace Mintokei.Runner.Host.RemoteExecution;

/// <summary>
/// Recovers a remote process handle for a correlation whose in-memory handle
/// was lost — e.g. after an API restart, when a runner replays output for a
/// correlation the API no longer has a live <see cref="RemoteProcessHandle"/>
/// for.
///
/// This is a host seam: the implementation lives in the product (Api) because
/// recovery walks product tables (AgentTask → RemoteCorrelationId) and re-drives
/// the agent-execution pipeline. The runner-host transport/dispatcher only needs
/// to ask "recover a handle for this correlation" and get one back, so it depends
/// on this interface rather than the product-coupled implementation.
/// </summary>
public interface IRemoteProcessRecovery
{
    /// <summary>
    /// Attempts to recover a remote process handle for the given correlation.
    /// Returns the handle if recovery succeeds, or null if the correlation
    /// cannot be mapped back to a task. Thread-safe: concurrent calls for the
    /// same correlation await the same recovery.
    /// </summary>
    Task<RemoteProcessHandle?> TryRecoverAsync(Guid correlationId, Guid machineId);
}
