namespace Mintokei.AgentControlPlane;

/// <summary>
/// The runner side of the control plane as a public seam, so transports (e.g. <c>RunnerHub</c>) can
/// register connected runners through the DB-free <c>DefaultAgentControlPlane</c> without depending on its internal
/// session API. Implemented by <c>DefaultAgentControlPlane</c>; registered as a singleton pointing at the same instance.
/// </summary>
public interface IRunnerRegistry
{
    IReadOnlyList<RunnerInfo> ListRunners();
    bool IsRunnerConnected(Guid machineId);

    /// <summary>Resolves the machine behind a live transport connection id, if any.</summary>
    Guid? GetMachineId(string connectionId);

    /// <summary>Resolves the live transport connection id for a machine, if connected.</summary>
    string? GetConnectionId(Guid machineId);

    /// <summary>Registers a connected (already-authenticated) runner and raises <see cref="RunnerConnected"/>.</summary>
    RunnerInfo ConnectRunner(Guid machineId, string connectionId);

    /// <summary>Deregisters by transport connection id (connection-scoped, so a stale disconnect can't
    /// evict a runner that has since reconnected) and raises <see cref="RunnerDisconnected"/>.</summary>
    void DisconnectRunnerByConnection(string connectionId);

    /// <summary>Deregisters by machine id — for evictions that aren't tied to a transport disconnect
    /// (heartbeat timeout, machine removal) — and raises <see cref="RunnerDisconnected"/>. Routing these
    /// through the registry (instead of poking the tracker directly) keeps the control plane's event
    /// stream complete: a silent death then projects Offline like any other disconnect.</summary>
    void DisconnectRunner(Guid machineId);

    event Action<RunnerInfo>? RunnerConnected;
    event Action<RunnerInfo>? RunnerDisconnected;
}
