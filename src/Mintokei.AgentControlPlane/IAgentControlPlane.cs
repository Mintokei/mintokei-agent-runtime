using Mintokei.AgentEngine;
using Mintokei.AgentEngine.AgentTools;

namespace Mintokei.AgentControlPlane;

/// <summary>
/// The one front door to the agent control plane — inject <em>this</em> and you can spawn/adopt/stop
/// sessions <em>and</em> connect/disconnect/list runners without wiring up several services. It is the
/// broad facade over the single <c>DefaultAgentControlPlane</c> instance: session lifecycle lives here, runner
/// presence is inherited from <see cref="IRunnerRegistry"/>. Capacity/admission stays on the separate
/// <see cref="ICapacityLedger"/> — an advanced concern most consumers never touch — but the same
/// <c>DefaultAgentControlPlane</c> backs all three, so they always agree.
///
/// Register everything with <c>services.AddAgentControlPlane()</c>. Consumers that only need one slice
/// (e.g. a transport that just reports presence) can still inject the narrow <see cref="IRunnerRegistry"/>
/// instead — interface segregation is preserved; this just adds a convenience aggregate on top.
///
/// Exposed as an interface (not the concrete <c>DefaultAgentControlPlane</c>) so consumers stay mockable and the
/// control plane can keep evolving its internals without breaking them.
/// </summary>
public interface IAgentControlPlane : IRunnerRegistry
{
    // ── Session lifecycle: the control plane spawns AND tracks in one call ──

    /// <summary>
    /// Spawns a session for <paramref name="spec"/> — locally when <paramref name="runnerMachineId"/>
    /// is null, otherwise on that connected runner — registers it under its own generated session id,
    /// runs the handshake, and returns it ready for <see cref="IAgentSession.SendMessageAsync"/>. The
    /// caller consumes <see cref="IAgentSession.Output"/> and calls <see cref="StopSessionAsync"/> when
    /// done. Use the <paramref name="sessionKey"/> overload to register under a caller-chosen key.
    /// </summary>
    Task<IAgentSession> StartSessionAsync(
        AgentSessionSpec spec, Guid? runnerMachineId = null, Guid? agentId = null,
        AgentSessionOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Spawns a session registered under a caller-supplied <paramref name="sessionKey"/> — an opaque id
    /// the control plane never interprets (Mintokei passes the <c>AgentTask.Id</c>; another embedder
    /// passes whatever it keys sessions by). <see cref="GetSession"/> / <see cref="SetIdleSince"/> /
    /// <see cref="StopSessionAsync"/> then speak that key. Null uses the session's own generated id.
    /// </summary>
    Task<IAgentSession> StartSessionAsync(
        Guid? sessionKey, AgentSessionSpec spec, Guid? runnerMachineId = null, Guid? agentId = null,
        AgentSessionOptions? options = null, CancellationToken ct = default);

    /// <summary>Stops and deregisters a session (disposing its process) by its registry key, raising
    /// <see cref="SessionEnded"/>. Returns false if the key is unknown.</summary>
    Task<bool> StopSessionAsync(Guid key);

    /// <summary>Snapshot of the currently registered sessions (metadata only — resolve the live handle
    /// with <see cref="GetSession"/>).</summary>
    IReadOnlyList<AgentSessionInfo> ListSessions();

    /// <summary>Raised when a session is spawned via <see cref="StartSessionAsync(AgentSessionSpec, Guid?, Guid?, AgentSessionOptions?, CancellationToken)"/>
    /// or mirrored in via <see cref="RegisterSession"/> — the hook a persistence sidecar subscribes to.</summary>
    event Action<AgentSessionInfo>? SessionStarted;

    /// <summary>Raised when a session is removed via <see cref="StopSessionAsync"/> or <see cref="DeregisterSession"/>.</summary>
    event Action<AgentSessionInfo>? SessionEnded;

    // ── Session registry: adopt / look up / track idle for sessions the caller owns ──

    /// <summary>Registers an already-created session — one the caller spawned and handshakes itself —
    /// under <paramref name="sessionKey"/> (Mintokei: the <c>AgentTask.Id</c>). The caller owns the
    /// session's lifecycle and pairs this with <see cref="DeregisterSession"/>.</summary>
    void RegisterSession(Guid sessionKey, IAgentSession session, AgentToolKey tool, Guid? machineId = null, Guid? agentId = null);

    /// <summary>Removes a session registered via <see cref="RegisterSession"/>, but only if the entry
    /// under <paramref name="sessionKey"/> is still <paramref name="session"/> (so a concurrent
    /// re-registration isn't clobbered). Returns false if nothing matched. Does not dispose.</summary>
    bool DeregisterSession(Guid sessionKey, IAgentSession session);

    /// <summary>Looks up a live session by its registry key, or null.</summary>
    IAgentSession? GetSession(Guid key);

    /// <summary>Marks a session idle since the given instant (an idle CLI still holds a capacity slot).</summary>
    void SetIdleSince(Guid key, DateTimeOffset idleSince);

    /// <summary>Clears a session's idle marker (it became active again).</summary>
    void ClearIdleSince(Guid key);
}
