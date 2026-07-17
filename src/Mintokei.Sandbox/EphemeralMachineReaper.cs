namespace Mintokei.Sandbox;

/// <summary>
/// GC-relevant view of a runner machine for <see cref="EphemeralMachineReaper"/>. The embedder maps its
/// own machine row to this — e.g. from a RunnerMachine: <c>IsEphemeral</c>, <c>Status == Offline</c>, and
/// <c>LastSeen = DisconnectedAt ?? CreatedAt</c>. <c>Name</c> must match the sandbox/container name.
/// </summary>
public sealed record SandboxMachine(Guid Id, string Name, bool IsEphemeral, bool Offline, DateTimeOffset LastSeen);

/// <summary>
/// Pure policy for garbage-collecting ephemeral (single-use sandbox) machine rows. The embedder queries its
/// machines plus the live sandbox names (<see cref="ISandboxRuntime.ListManagedAsync"/>), calls this, and
/// deletes the returned ids — so the retirement policy lives here, not duplicated in the product.
/// </summary>
public static class EphemeralMachineReaper
{
    /// <summary>
    /// Machine ids safe to delete: <c>IsEphemeral</c> and <c>Offline</c>, whose container is <b>confirmed
    /// gone</b> (name not in <paramref name="liveSandboxNames"/>), and last seen longer ago than
    /// <paramref name="retentionAfterDisconnect"/>. Never returns persistent runners, online machines, or
    /// ones whose container is still running — a disconnected-but-running sandbox may reconnect and resume,
    /// so its row is kept. The retention window is a grace against the observe-race and the unobservable
    /// (multi-host) case where a container can't be listed.
    /// </summary>
    public static IReadOnlyList<Guid> SelectPrunable(
        IEnumerable<SandboxMachine> machines,
        IReadOnlySet<string> liveSandboxNames,
        DateTimeOffset now,
        TimeSpan retentionAfterDisconnect)
    {
        var prunable = new List<Guid>();
        foreach (var machine in machines)
        {
            if (!machine.IsEphemeral || !machine.Offline)
                continue;
            if (liveSandboxNames.Contains(machine.Name))
                continue; // container still running — may reconnect and resume; keep the row
            if (now - machine.LastSeen >= retentionAfterDisconnect)
                prunable.Add(machine.Id);
        }

        return prunable;
    }
}
