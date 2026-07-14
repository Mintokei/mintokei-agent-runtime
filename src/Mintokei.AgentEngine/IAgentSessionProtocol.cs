using System.Text.Json;
using Mintokei.AgentEngine.CommandRunner;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine;

/// <summary>
/// The per-backend seam of an <see cref="AgentSession"/> — everything that differs by CLI wire
/// format. The session engine owns the generic machinery (the read pump, the control-response
/// TCS round-trip, the interaction policy, cancellation); a protocol supplies only the bytes.
///
/// Mirrors the protocol slots on <c>AgentExecutionServiceBase</c>, but scoped to one session rather
/// than resolved by task id — so it needs no process store, DB, or message stream.
/// </summary>
public interface IAgentSessionProtocol
{
    /// <summary>Creates the stream parser for this session (frame → <see cref="AgentStreamOutput"/>).</summary>
    IAgentStreamParser CreateParser(Guid sessionId);

    /// <summary>Parses one raw stdout line into a JSON frame; false for non-JSON lines (skipped).</summary>
    bool TryParseFrame(string line, out JsonElement frame);

    /// <summary>Classifies a routed control-response frame into a result-or-fault (purely).</summary>
    ControlResponseOutcome ClassifyControlResponse(JsonElement raw);

    /// <summary>The decision that ACCEPTS an interaction in this backend's reply vocabulary
    /// (Claude <c>"allow"</c>; Codex/ACP <c>"accept"</c>). Used by <see cref="InteractionMode.AutoApprove"/>
    /// and MCP session auto-accept — a shared string would be wrong, because each backend's reply
    /// builder maps decision text differently (Claude treats any non-<c>"allow"</c> as deny).</summary>
    UserInteractionResponse AcceptDecision { get; }

    /// <summary>The decision that DENIES an interaction in this backend's reply vocabulary. Used by
    /// <see cref="InteractionMode.Deny"/>.</summary>
    UserInteractionResponse DenyDecision { get; }

    /// <summary>Writes a correlated request frame (the send half of a round-trip). The session
    /// registers the waiter and awaits the reply keyed on <paramref name="requestId"/>.</summary>
    Task SendRequestAsync(
        IProcessHandle handle, string requestId, string method, object? payload, CancellationToken ct);

    /// <summary>Completes the protocol handshake against the (already-pumping) process.</summary>
    Task HandshakeAsync(AgentSession session, bool resume, CancellationToken ct);

    /// <summary>Writes one user turn to the running CLI — text plus the turn's optional context block
    /// and image attachments, incorporated per the backend's wire format.</summary>
    Task SendTurnAsync(AgentSession session, SessionTurn turn, CancellationToken ct);

    /// <summary>Signals interrupt to the running CLI (fire-and-forget for backends that don't ack).</summary>
    Task SendInterruptAsync(AgentSession session, CancellationToken ct);

    /// <summary>Triggers compaction on the running session. Throw <see cref="NotSupportedException"/>
    /// when the backend can't be driven programmatically.</summary>
    Task CompactAsync(AgentSession session, string? instructions, CancellationToken ct);

    /// <summary>Rolls back the last <paramref name="numTurns"/> turns in-place on the running
    /// session (Codex <c>thread/rollback</c>). Throw <see cref="NotSupportedException"/> for
    /// backends without a native rollback — their rewind kills and respawns the process instead.</summary>
    Task RollbackAsync(AgentSession session, int numTurns, CancellationToken ct);

    /// <summary>Applies a mid-session config diff (old→new) via the backend's control requests;
    /// returns true if anything was applied.</summary>
    Task<bool> ApplyConfigAsync(
        AgentSession session, Dictionary<string, string?> oldConfig, Dictionary<string, string?> newConfig, CancellationToken ct);
}
