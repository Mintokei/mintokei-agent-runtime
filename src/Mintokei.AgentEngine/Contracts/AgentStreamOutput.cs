using System.Text.Json;

namespace Mintokei.AgentEngine.Contracts;

/// <summary>
/// Backend-agnostic event produced by a per-agent stream parser (Claude stream-json,
/// Codex / ACP JSON-RPC) from one frame of the CLI's output — the engine's one output vocabulary.
///
/// Most cases are <em>one-way transcript</em> — "publish a message" / "emit a delta" — which the
/// host's sink acts on without the live process handle. Two cases are <em>bidirectional</em> and
/// carry only pure data — the session's dispatch acts on them <em>with</em> the handle:
/// <see cref="InteractionRequested"/> (a permission/question the CLI blocks on) and
/// <see cref="ControlResponseReceived"/> (a reply to a request we sent, routed to its pending waiter).
/// </summary>
public abstract record AgentStreamOutput;

/// <summary>An <see cref="AgentMessage"/> to publish through the host's persistence channel.</summary>
public sealed record MessageOutput(AgentMessage Message) : AgentStreamOutput;

/// <summary>A real-time <see cref="DeltaPayload"/> to publish through the host's SSE delta channel.</summary>
public sealed record DeltaOutput(DeltaPayload Payload) : AgentStreamOutput;

/// <summary>
/// The context-window compaction state flipped: <c>true</c> when compaction starts,
/// <c>false</c> when it finishes. Drives the host's persistent "Compacting…" banner.
/// </summary>
public sealed record CompactingChanged(bool Active) : AgentStreamOutput;

/// <summary>
/// The agent finished a turn. Carries the raw provider result element (forwarded to the host's
/// turn-completed event), whether the user interrupted it, and a normalized
/// <see cref="TurnFailure"/> when the turn ended in error (null on success/interrupt).
/// </summary>
public sealed record TurnEnded(JsonElement? RawResult, bool IsInterrupted, TurnFailure? Failure) : AgentStreamOutput;

/// <summary>
/// The CLI reported a session/thread id on the stream (Claude system + result events).
/// The host persists it and updates the in-memory task, deduping if unchanged.
/// </summary>
public sealed record SessionIdChanged(string SessionId) : AgentStreamOutput;

/// <summary>
/// The CLI reported the provider's own id (UUID) for the latest user/assistant message.
/// The host back-fills it onto the most recent persisted row of that role — used later as the
/// rewind/fork anchor.
/// </summary>
public sealed record ExternalMessageIdAssigned(MessageRole Role, string ExternalId) : AgentStreamOutput;

/// <summary>A new turn began without user input (Claude's Monitor / ScheduleWakeup wake).
/// The host resumes a Processed task.</summary>
public sealed record TurnStarted : AgentStreamOutput;

/// <summary>Drop the accumulated streaming-delta snapshot now that its fragments have
/// been consolidated into persisted messages (Claude, after each assistant event).</summary>
public sealed record ClearDeltaSnapshot : AgentStreamOutput;

/// <summary>Flush any partial streaming-delta content as concrete messages before a
/// boundary (Claude, on an interrupted turn).</summary>
public sealed record FlushDeltaSnapshot : AgentStreamOutput;

// ── Bidirectional cases (pure data; the session's dispatch acts on these with the process handle) ──

/// <summary>
/// The CLI asked a permission/question and is blocked on its own request until we reply
/// (Claude <c>control_request/can_use_tool</c>, Codex approval/user-input/elicitation, ACP
/// <c>session/request_permission</c>). <paramref name="Message"/> (with its
/// <c>UserInteraction</c>, including <c>ReplyContext</c>) is the prompt to publish;
/// <paramref name="CacheKey"/> is the Codex MCP session-cache key (null for everything else). The
/// host publishes the prompt, fires its interaction event, and registers the reply; for Codex it
/// consults/updates the session cache.
///
/// <para><paramref name="NotifyContent"/> / <paramref name="NotifyToolName"/> /
/// <paramref name="NotifyCommand"/> are the parser-computed summary the host feeds into its
/// out-of-app notification (they don't always equal the message's own fields — e.g. a question's
/// notify-content is the extracted question text). The message id and type come from
/// <paramref name="Message"/>.</para>
/// </summary>
public sealed record InteractionRequested(
    string RequestId,
    AgentMessage Message,
    string? CacheKey,
    string? NotifyContent,
    string? NotifyToolName,
    string? NotifyCommand) : AgentStreamOutput;

/// <summary>
/// A reply to a request we sent arrived (Claude <c>control_response</c>, or a JSON-RPC frame with
/// an <c>id</c> and no <c>method</c>). The session's pump routes it to the matching pending waiter
/// registered by <c>AgentSession.SendRequestAndWaitAsync</c>. <paramref name="Id"/> is the
/// already-extracted correlation key; <paramref name="Raw"/> is the whole frame (an error field,
/// if present, faults the waiter).
/// </summary>
public sealed record ControlResponseReceived(string Id, JsonElement Raw) : AgentStreamOutput;
