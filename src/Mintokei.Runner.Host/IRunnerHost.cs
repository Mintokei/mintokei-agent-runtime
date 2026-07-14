using Mintokei.Runner.Contracts.Messages;

namespace Mintokei.Runner.Host;

/// <summary>
/// Optional reaction seam between the runner transport (the gRPC server + durable outbox) and the
/// application that owns what a runner connection or its output <em>means</em>. The transport raises
/// these; the consuming application reacts to the ones it cares about. Every member has a no-op default
/// implementation, so an implementer overrides <em>only</em> what it needs — a host with no product
/// state to reconcile can skip the interface entirely (the transport falls back to this project's
/// <c>NullRunnerHost</c>). Nothing here references product concepts (AgentTask / Workspace / Agent) —
/// only transport facts and dependency-free wire types.
///
/// This is <em>reactions only</em>. The one non-reaction the transport needs — the CLI-probe list the
/// handshake hands back — is a <em>pull with a return value</em>, so it is configuration rather than an
/// event: set <c>RunnerHostOptions.CliProbesProvider</c> via <c>AddRunnerHostCore(o =&gt; ...)</c>
/// instead. See <c>docs/runner-host-extraction-plan.md</c> (§4).
/// </summary>
public interface IRunnerHost
{
    /// <summary>
    /// A runner has (re)connected and completed its handshake. <paramref name="activeCorrelationIds"/>
    /// is the set of correlations the runner still has live; <c>null</c> means the runner did not
    /// report them (older build) and the consumer should treat everything as gone. This is NOT a
    /// pure notification — the consumer may re-open output streams or enqueue kills as a side-effect
    /// while reconciling its in-flight work (the transport <c>await</c>s it before completing connect).
    /// </summary>
    Task OnRunnerConnectedAsync(Guid machineId, IReadOnlyList<Guid>? activeCorrelationIds) => Task.CompletedTask;

    /// <summary>
    /// A runner reported the CLIs (and their models) installed on its host, after probing the
    /// binaries listed in the handshake response. The consumer persists them as it sees fit.
    /// </summary>
    Task OnInstalledClisReportedAsync(Guid machineId, IReadOnlyList<InstalledCli> installed) => Task.CompletedTask;

    /// <summary>
    /// A remote file-system change was reported for an opaque id (which may identify a workspace OR
    /// an agent task — the consumer disambiguates). The runner supplies only the id; any path and
    /// machine association are resolved on the consumer side.
    /// </summary>
    Task OnRemoteFileSystemChangedAsync(Guid id) => Task.CompletedTask;

    /// <summary>
    /// A process completed on the runner for the given transport correlation. The consumer resolves
    /// which of its units of work (if any) that correlation maps to and sweeps anything left dangling
    /// (e.g. orphaned tool-use / prompt rows). A no-op if the correlation is unknown to the consumer.
    /// </summary>
    Task OnOrphanCorrelationAsync(Guid correlationId) => Task.CompletedTask;

    /// <summary>
    /// A runner disconnected. The consumer tears down its in-process state for that machine — drop
    /// live process contexts, mark the machine's in-flight units of work as disconnected so they stay
    /// resumable. Library-owned transport bookkeeping (remote-handle state) stays on the transport.
    /// </summary>
    Task OnRunnerDisconnectedAsync(Guid machineId) => Task.CompletedTask;

    /// <summary>
    /// A runner (re)opened its file-watcher stream. The consumer re-issues its active file-watch
    /// subscriptions for that machine: after a reconnect the runner starts with no watchers running,
    /// so any workspace/task the product is currently watching there must be re-established over the
    /// freshly-opened stream. A no-op if the consumer has nothing watched on that machine.
    /// </summary>
    Task OnWatcherChannelOpenedAsync(Guid machineId) => Task.CompletedTask;
}
