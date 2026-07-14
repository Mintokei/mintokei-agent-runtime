namespace Mintokei.AgentEngine.Contracts;

/// <summary>
/// Lightweight real-time streaming delta produced by the engine's parsers. Not persisted — the
/// host wraps it in its own stream envelope and emits it as an SSE "delta" event.
/// </summary>
public abstract record DeltaPayload;

public sealed record ContentDeltaPayload(
    string DeltaType,
    int BlockIndex,
    string Delta,
    string? ToolName = null,
    string? ParentToolUseId = null) : DeltaPayload;

/// <summary>
/// Opens a new content block.
/// <para>
/// <see cref="ParentToolUseId"/> echoes the surrounding <c>tool_use_id</c> when this block
/// originates from inside a sub-agent invocation (Claude Code's <c>Agent</c> tool). The
/// frontend uses it to render the live streaming preview inside the parent
/// <c>SubAgentExecution</c> container instead of at the chat tail. Null for main-agent blocks
/// and for backends that don't expose sub-agent semantics (Codex, ACP).
/// </para>
/// </summary>
public sealed record BlockStartPayload(
    int BlockIndex,
    string BlockType,
    string? ToolName = null,
    string? ParentToolUseId = null) : DeltaPayload;

public sealed record BlockStopPayload(int BlockIndex) : DeltaPayload;

public sealed record TurnPayload(bool IsStart) : DeltaPayload;

/// <summary>
/// Per-turn context-window usage snapshot. Emitted whenever the underlying CLI reports a fresh
/// token count (Claude: per assistant message + final result; Codex: <c>thread/tokenUsageUpdated</c>;
/// ACP: <c>usage_update</c>).
/// </summary>
/// <param name="UsedTokens">Tokens occupying context for the latest turn. Nullable so a per-turn
/// emit that only carries window/cost can leave the previously-reported footprint in place.</param>
/// <param name="WindowTokens">Total context-window size for the active model. Null if not reported yet.</param>
/// <param name="Model">Model identifier that produced the usage, when known.</param>
/// <param name="CostUsd">Cumulative dollar cost for the session, when the CLI reports it.</param>
public sealed record ContextUsagePayload(
    long? UsedTokens,
    long? WindowTokens,
    string? Model,
    double? CostUsd) : DeltaPayload;
