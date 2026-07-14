using System.Collections.Concurrent;
using System.Linq;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine.AgentTools;

namespace Mintokei.AgentControlPlane;

/// <summary>
/// DB-free agent control plane: the live runtime facade over connected runners and active sessions.
/// Composes the <see cref="AgentSessionLauncher"/> (local/remote spawn) and the
/// <see cref="RunnerConnectionTracker"/> (the in-memory connected-runner registry) with an in-memory
/// session registry, and raises <c>RunnerConnected</c>/<c>RunnerDisconnected</c> and
/// <c>SessionStarted</c>/<c>SessionEnded</c> so a persistence sidecar can subscribe (the
/// DB-owner→subscriber inversion) — <em>without</em> this class ever touching a DbContext.
///
/// The session registry doubles as the capacity slot-book: it answers the same live/active/pending
/// counts as <see cref="Application.Common.Services.IAgentProcessStore"/> and owns the
/// <see cref="MachineAdmissionControl"/> (pending claims + per-machine lock). This is the read/reserve
/// surface Stage C points <c>AgentInstanceLimitEnforcer</c> at once prod sessions run on the engine.
///
/// Prod runner connections are routed through this class: <c>RunnerHub</c> (SignalR) authenticates via
/// the pluggable <c>IRunnerAuthenticator</c> and funnels connect/disconnect through
/// <see cref="ConnectRunner"/>/<see cref="DisconnectRunnerByConnection"/>, and the non-transport
/// evictions (heartbeat timeout, machine removal) go through <see cref="DisconnectRunner"/> — so the
/// runner events fire for every prod connection change and the persistence sidecar's projection is
/// complete. Session rehydration on reconnect (<c>ResumeOutputStreamAsync</c>'s engine branch)
/// re-registers surviving sessions after an API restart. See <c>docs/agent-server-design.md</c> and
/// <c>docs/stage-e-rehydration.md</c>.
///
/// Stays <c>internal</c>: its whole surface is reachable through the broad <see cref="IAgentControlPlane"/>
/// facade (session lifecycle + runner presence) plus the narrow <see cref="ICapacityLedger"/>, so consumers
/// bind to those contracts and this concrete can keep evolving freely.
/// </summary>
internal sealed class DefaultAgentControlPlane : IRunnerRegistry, ICapacityLedger, IAgentControlPlane
{
    private readonly AgentSessionLauncher _launcher;
    private readonly RunnerConnectionTracker _runners;
    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();
    // The session registry doubles as the capacity slot-book: pending claims + per-machine locks.
    private readonly MachineAdmissionControl _admission = new();

    public DefaultAgentControlPlane(AgentSessionLauncher launcher, RunnerConnectionTracker runners)
    {
        _launcher = launcher;
        _runners = runners;
    }

    public event Action<RunnerInfo>? RunnerConnected;
    public event Action<RunnerInfo>? RunnerDisconnected;
    public event Action<AgentSessionInfo>? SessionStarted;
    public event Action<AgentSessionInfo>? SessionEnded;

    // ── Runners ──

    /// <summary>The runners connected right now (live registry — no DB).</summary>
    public IReadOnlyList<RunnerInfo> ListRunners()
        => [.. _runners.ConnectedMachineIds.Select(id => new RunnerInfo(id, _runners.GetConnectionId(id)))];

    public bool IsRunnerConnected(Guid machineId) => _runners.IsConnected(machineId);

    /// <summary>Resolves the machine behind a live transport connection id, if any.</summary>
    public Guid? GetMachineId(string connectionId) => _runners.GetMachineId(connectionId);

    /// <summary>Resolves the live transport connection id for a machine, if connected.</summary>
    public string? GetConnectionId(Guid machineId) => _runners.GetConnectionId(machineId);

    /// <summary>Registers a connected runner (an already-authenticated identity + its transport
    /// connection id) and raises <c>RunnerConnected</c>. Auth/transport is the caller's concern — this
    /// only records the live connection.</summary>
    public RunnerInfo ConnectRunner(Guid machineId, string connectionId)
    {
        _runners.Register(machineId, connectionId);
        var info = new RunnerInfo(machineId, connectionId);
        RunnerConnected?.Invoke(info);
        return info;
    }

    /// <summary>Deregisters a runner by machine id and raises <c>RunnerDisconnected</c> (programmatic use).</summary>
    public void DisconnectRunner(Guid machineId)
    {
        var connectionId = _runners.GetConnectionId(machineId);
        _runners.Unregister(machineId);
        RunnerDisconnected?.Invoke(new RunnerInfo(machineId, connectionId));
    }

    /// <summary>Deregisters by transport connection id (connection-scoped — a stale disconnect can't
    /// evict a runner that has since reconnected) and raises <c>RunnerDisconnected</c>.</summary>
    public void DisconnectRunnerByConnection(string connectionId)
    {
        var machineId = _runners.GetMachineId(connectionId);
        _runners.UnregisterByConnection(connectionId);
        if (machineId is { } id)
            RunnerDisconnected?.Invoke(new RunnerInfo(id, connectionId));
    }

    // ── Sessions ──

    /// <summary>
    /// Starts a session for <paramref name="spec"/> — locally when <paramref name="runnerMachineId"/>
    /// is null, otherwise on that connected runner — registered under its own generated session id.
    /// Use the <c>sessionKey</c> overload to register under a caller-chosen key. The caller consumes
    /// <see cref="IAgentSession.Output"/>; call <see cref="StopSessionAsync"/> when done.
    /// </summary>
    public Task<IAgentSession> StartSessionAsync(
        AgentSessionSpec spec, Guid? runnerMachineId = null, Guid? agentId = null,
        AgentSessionOptions? options = null, CancellationToken ct = default)
        => StartSessionAsync(sessionKey: null, spec, runnerMachineId, agentId, options, ct);

    /// <summary>
    /// Starts a session registered under a caller-supplied <paramref name="sessionKey"/> — an <em>opaque</em>
    /// id the control plane never interprets (Mintokei passes the <c>AgentTask.Id</c>; another embedder
    /// passes whatever it keys sessions by). <see cref="GetSession"/> / <see cref="SetIdleSince"/> /
    /// <see cref="StopSessionAsync"/> and every <see cref="CapacitySlot"/> then speak that key. Null uses
    /// the session's own generated id. Keeping the key opaque is what makes this a reusable,
    /// orchestration-agnostic control plane: no task / workspace / DB concept leaks in — an embedder that
    /// needs, say, a workspace id for teardown resolves it from the key on its own side.
    /// </summary>
    public async Task<IAgentSession> StartSessionAsync(
        Guid? sessionKey, AgentSessionSpec spec, Guid? runnerMachineId = null, Guid? agentId = null,
        AgentSessionOptions? options = null, CancellationToken ct = default)
    {
        var session = await _launcher.StartSessionAsync(spec, runnerMachineId, options, ct);
        var key = sessionKey ?? session.SessionId;
        var info = new AgentSessionInfo(key, session.SessionId, spec.Tool, runnerMachineId);
        _sessions[key] = new Entry(session, key, info, agentId);
        SessionStarted?.Invoke(info);
        return session;
    }

    /// <summary>
    /// Registers an <em>already-created</em> session — one the caller spawned and handshakes itself
    /// (the execution service's engine path wraps a handle it owns) — under <paramref name="sessionKey"/>,
    /// raising <c>SessionStarted</c>. Unlike <see cref="StartSessionAsync(Guid?, AgentSessionSpec, Guid?, Guid?, AgentSessionOptions?, CancellationToken)"/>
    /// it neither spawns nor owns disposal: the caller keeps the session's lifecycle and pairs this with
    /// <see cref="DeregisterSession"/>. This is the Stage C dual-write bridge — prod keeps its own store;
    /// engine-backed sessions are mirrored here so the registry reflects the live runtime.
    /// </summary>
    public void RegisterSession(
        Guid sessionKey, IAgentSession session, AgentToolKey tool, Guid? machineId = null, Guid? agentId = null)
    {
        var info = new AgentSessionInfo(sessionKey, session.SessionId, tool, machineId);
        _sessions[sessionKey] = new Entry(session, sessionKey, info, agentId);
        SessionStarted?.Invoke(info);
    }

    /// <summary>
    /// Removes a session registered via <see cref="RegisterSession"/> — but only if the entry under
    /// <paramref name="sessionKey"/> is still <paramref name="session"/>, so a concurrent re-registration
    /// under the same key (e.g. a mid-turn respawn) is never clobbered. Raises <c>SessionEnded</c> when it
    /// removes; does NOT dispose (the caller owns the lifecycle). Returns false if nothing matched.
    /// </summary>
    public bool DeregisterSession(Guid sessionKey, IAgentSession session)
    {
        if (!_sessions.TryGetValue(sessionKey, out var entry) || !ReferenceEquals(entry.Session, session))
            return false;

        // Atomic compare-and-remove: drops the entry only if it's still this exact one.
        if (!_sessions.TryRemove(new KeyValuePair<Guid, Entry>(sessionKey, entry)))
            return false;

        SessionEnded?.Invoke(entry.Info);
        return true;
    }

    /// <summary>Snapshot of the currently registered sessions.</summary>
    public IReadOnlyList<AgentSessionInfo> ListSessions() => [.. _sessions.Values.Select(e => e.Info)];

    /// <summary>Looks up a live session by its registry key — the caller-supplied <c>sessionKey</c>
    /// (Mintokei: the <c>AgentTask.Id</c>), or the session's own id when none was supplied.</summary>
    public IAgentSession? GetSession(Guid key)
        => _sessions.TryGetValue(key, out var entry) ? entry.Session : null;

    /// <summary>Stops and deregisters a session (disposes its process) by its registry key. Returns
    /// false if unknown.</summary>
    public async Task<bool> StopSessionAsync(Guid key)
    {
        if (!_sessions.TryRemove(key, out var entry))
            return false;

        await entry.Session.DisposeAsync();
        SessionEnded?.Invoke(entry.Info);
        return true;
    }

    // ── Capacity (the session registry as the machine slot-book) ──
    //
    // Counts mirror IAgentProcessStore exactly: "live" = registered and not exited (an exited
    // session still lingers in the registry until teardown but holds no slot); "active" = live and
    // not idle (idle slots are evictable, so dispatchers treat them as available). Machine matching
    // here is exact — the local "null-or-local-machine-id" fold lives in the enforcer, which reads
    // GetSlots() for that. This is the read surface Stage C points the enforcer at.

    /// <summary>Live sessions across all machines (registered and not exited).</summary>
    public int LiveSessionCount => _sessions.Values.Count(e => !e.Session.HasExited);

    /// <summary>Live sessions for one tool.</summary>
    public int CountByToolKey(AgentToolKey tool)
        => _sessions.Values.Count(e => e.Info.Tool == tool && !e.Session.HasExited);

    /// <summary>Live sessions on a machine (includes idle — an idle CLI still holds the slot).</summary>
    public int CountByMachine(Guid machineId)
        => _sessions.Values.Count(e => e.Info.RunnerMachineId == machineId && !e.Session.HasExited);

    /// <summary>Active (non-idle) live sessions on a machine.</summary>
    public int CountActiveByMachine(Guid machineId)
        => _sessions.Values.Count(e =>
            e.Info.RunnerMachineId == machineId && e.IdleSince is null && !e.Session.HasExited);

    /// <summary>Live sessions on a machine for one agent.</summary>
    public int CountByMachineAndAgent(Guid machineId, Guid agentId)
        => _sessions.Values.Count(e =>
            e.Info.RunnerMachineId == machineId && e.AgentId == agentId && !e.Session.HasExited);

    /// <summary><see cref="ICapacityLedger.GetSlots"/> — a capacity snapshot of every registered session:
    /// everything a consumer needs to count slots, apply the local null-or-machine-id fold, pick idle
    /// eviction victims, and log. <see cref="CapacitySlot.Key"/> = the opaque registry key.</summary>
    public IReadOnlyList<CapacitySlot> GetSlots()
        => [.. _sessions.Values.Select(e => new CapacitySlot(
            e.Key, e.Info.SessionId, e.Info.RunnerMachineId,
            e.AgentId, e.Info.Tool, e.IdleSince, e.Session.HasExited))];

    /// <summary>Marks a session idle since <paramref name="idleSince"/> by its registry key (no-op if unknown).</summary>
    public void SetIdleSince(Guid key, DateTimeOffset idleSince)
    {
        if (_sessions.TryGetValue(key, out var e)) e.IdleSince = idleSince;
    }

    /// <summary>Clears a session's idle marker by its registry key — it's processing a turn again (no-op if unknown).</summary>
    public void ClearIdleSince(Guid key)
    {
        if (_sessions.TryGetValue(key, out var e)) e.IdleSince = null;
    }

    // ── Admission (pending claims + per-machine lock — the atomic cap-check + reserve) ──

    public int GetPendingClaimsByMachine(Guid? machineId)
        => _admission.GetPendingClaimsByMachine(machineId);

    public int GetPendingClaimsByMachineAndAgent(Guid? machineId, Guid agentId)
        => _admission.GetPendingClaimsByMachineAndAgent(machineId, agentId);

    public IDisposable AddPendingClaim(Guid? machineId, Guid? agentId)
        => _admission.AddPendingClaim(machineId, agentId);

    public SemaphoreSlim GetMachineLock(Guid? machineId)
        => _admission.GetMachineLock(machineId);

    private sealed class Entry(IAgentSession session, Guid key, AgentSessionInfo info, Guid? agentId)
    {
        public IAgentSession Session { get; } = session;
        // The opaque registry key the session was registered under (caller-supplied or the session id).
        public Guid Key { get; } = key;
        public AgentSessionInfo Info { get; } = info;
        public Guid? AgentId { get; } = agentId;
        public DateTimeOffset? IdleSince { get; set; }
    }
}

/// <summary>A connected runner in the live registry (machine id + its transport connection id).</summary>
public sealed record RunnerInfo(Guid MachineId, string? ConnectionId);

/// <summary>Metadata for a registered session (no live handle — that's <see cref="DefaultAgentControlPlane.GetSession"/>).
/// <see cref="Key"/> is the opaque registry key (Mintokei: the <c>AgentTask.Id</c>); a persistence sidecar
/// correlates <c>SessionStarted</c>/<c>SessionEnded</c> back to its own world through it.</summary>
public sealed record AgentSessionInfo(Guid Key, Guid SessionId, AgentToolKey Tool, Guid? RunnerMachineId);
