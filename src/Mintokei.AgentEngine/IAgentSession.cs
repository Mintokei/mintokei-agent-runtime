using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine;

/// <summary>
/// A single live agent conversation, decoupled from persistence, the task-message stream, domain
/// events and the process store. It owns exactly one CLI process plus the protocol plumbing
/// (handshake, turns, interrupts, the bidirectional control-response / interaction round-trips) and
/// surfaces the one-way transcript as a pull stream:
///
/// <code>
/// // runnerMachineId: null runs the CLI locally; a machine id runs it on a remote runner.
/// await using var session = await factory.CreateClaudeSessionAsync(spec, runnerMachineId: machineId);
/// await session.SendMessageAsync("hello");
/// await foreach (var output in session.Output)
///     // side effects (DB / stream / events) live out here, in the consumer
///     await sink.DispatchAsync(agentTask, output);
/// </code>
///
/// Interactions (permissions / questions / elicitations) are handled per
/// <see cref="AgentSessionOptions.InteractionMode"/>: auto-answered inline, or surfaced on
/// <see cref="Output"/> for the caller to answer with <see cref="RespondAsync"/>.
///
/// Internal because <see cref="Output"/> speaks the internal <see cref="AgentStreamOutput"/>
/// vocabulary — promoting this to a standalone library is the DTO-neutralisation step, separate
/// from this proof of concept.
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    /// <summary>The session's own opaque id (not a DB task id). Fed to the parser to tag messages.</summary>
    Guid SessionId { get; }

    /// <summary>The CLI's reported session/thread id once the handshake/first frames land, else null.</summary>
    string? AgentSessionId { get; }

    /// <summary>True once the underlying CLI process has exited. The control plane counts only live
    /// sessions when enforcing capacity — an exited session holds no machine slot.</summary>
    bool HasExited { get; }

    /// <summary>The one-way transcript: messages, deltas, turn boundaries, session-id changes, and —
    /// under <see cref="InteractionMode.Surface"/> — the <see cref="InteractionRequested"/> prompts.
    /// Completes when the process stream ends.</summary>
    IAsyncEnumerable<AgentStreamOutput> Output { get; }

    /// <summary>Starts the output pump then completes the protocol handshake. Called exactly once,
    /// before the session is used. The wrap-an-existing-handle integration path starts the Output
    /// consumer first, then calls this.</summary>
    Task StartAsync(bool resume, CancellationToken ct);

    /// <summary>
    /// Adopts an already-initialized process — Stage E rehydration after an API restart / runner
    /// reconnect. Starts the output pump but skips the protocol handshake entirely: the surviving CLI
    /// completed its <c>initialize</c>/<c>session/new</c> in the previous API instance's lifetime, and
    /// (as the legacy reattach proves) accepts follow-up turns without re-initializing. Call INSTEAD of
    /// <see cref="StartAsync"/>, exactly once, on a session whose id was seeded from persistence
    /// (<c>spec.ResumeSessionId</c> / <c>initialAgentSessionId</c>) — that seed is what turn sends stamp.
    /// </summary>
    Task AttachAsync(CancellationToken ct = default);

    /// <summary>Sends a plain-text user turn — shorthand for <c>SendTurnAsync(new SessionTurn(content))</c>.</summary>
    Task SendMessageAsync(string content, CancellationToken ct = default);

    /// <summary>Sends one user turn: text plus an optional first-turn context block and image
    /// attachments (the caller supplies them; the session inlines/attaches per the backend's wire format).</summary>
    Task SendTurnAsync(SessionTurn turn, CancellationToken ct = default);

    /// <summary>Answers a surfaced interaction: builds the wire reply via the keyed
    /// <see cref="IInteractionReplyBuilder"/> and writes it to the process. Returns false when the
    /// request id is unknown (already answered, or auto-handled).</summary>
    Task<bool> RespondAsync(string requestId, UserInteractionResponse decision, CancellationToken ct = default);

    /// <summary>Interrupts the current turn; the session stays alive for follow-ups. Returns false
    /// when the process has exited.</summary>
    Task<bool> InterruptAsync(CancellationToken ct = default);

    /// <summary>Triggers a context-window compaction on the running session. Throws
    /// <see cref="NotSupportedException"/> when the backend can't be driven programmatically.</summary>
    Task CompactAsync(string? instructions, CancellationToken ct = default);

    /// <summary>Rolls back the last <paramref name="numTurns"/> turns in-place on the running
    /// session (Codex <c>thread/rollback</c>); the session stays alive for the rewound follow-up
    /// turn. Throws <see cref="NotSupportedException"/> for backends without a native rollback.</summary>
    Task RollbackAsync(int numTurns, CancellationToken ct = default);

    /// <summary>Applies a mid-session config change (e.g. <c>set_model</c>) by diffing
    /// <paramref name="oldConfig"/> against <paramref name="newConfig"/> and sending the backend's
    /// control requests. Returns true if anything was applied.</summary>
    Task<bool> ApplyConfigAsync(
        Dictionary<string, string?> oldConfig, Dictionary<string, string?> newConfig, CancellationToken ct = default);
}
