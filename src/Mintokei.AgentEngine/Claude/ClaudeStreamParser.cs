using System.Text.Json;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Claude;

/// <summary>
/// Parses Claude Code <c>--output-format stream-json</c> events into the backend-agnostic
/// <see cref="AgentStreamOutput"/> vocabulary — both the one-way transcript (<c>system</c>,
/// <c>assistant</c>, <c>user</c>, <c>result</c>, <c>error</c>, <c>stream_event</c>,
/// <c>rate_limit_event</c>) and the bidirectional frames as <em>pure data</em>:
/// <c>control_request/can_use_tool</c> → <see cref="InteractionRequested"/> and
/// <c>control_response</c> → <see cref="ControlResponseReceived"/>. The service dispatch acts on
/// the bidirectional cases with the process handle; the parser never touches it.
///
/// Per-task and stateful. State that used to live on <c>AgentProcessContext</c> or in
/// the pump now lives here: the tool-use registry, the upstream turn failure captured
/// mid-turn, the stashed compact boundary awaiting its synthetic summary, whether the
/// in-flight compaction was user-initiated, and the last parent model (for picking the
/// right <c>modelUsage</c> context-window entry).
/// </summary>
internal sealed class ClaudeStreamParser : IAgentStreamParser
{
    private readonly ILogger _logger;
    private readonly Guid _agentTaskId;

    private readonly Dictionary<string, ClaudeCodeOutputParser.ToolUseInfo> _toolUseRegistry = new();

    /// <summary>Upstream error captured mid-turn (assistant error subtype, rate-limit
    /// rejection, or top-level error event), read by the result handler for classification.</summary>
    private TurnFailure? _pendingTurnFailure;

    /// <summary>A compact_boundary row stashed until the synthetic summary user event
    /// arrives so the two land as one atomic message.</summary>
    private AgentMessage? _pendingCompactBoundary;

    /// <summary>Set by the service when the user triggers <c>/compact</c>; consumed by the
    /// next compact_boundary so it's tagged Manual.</summary>
    private bool _pendingManualCompact;

    /// <summary>Most recent non-sub-agent assistant model, cached so the result event picks
    /// the parent's <c>modelUsage</c> entry (Claude lists models in API-completion order).</summary>
    private string? _lastParentModel;

    public ClaudeStreamParser(ILogger logger, Guid agentTaskId)
    {
        _logger = logger;
        _agentTaskId = agentTaskId;
    }

    /// <summary>Marks the next compact_boundary as user-initiated.</summary>
    public void NoteManualCompact() => _pendingManualCompact = true;

    /// <summary>Pump entry point (<see cref="IAgentStreamParser"/>). The parser carries its own
    /// task id, so <paramref name="agentTaskId"/> is ignored; only <paramref name="isInterrupted"/>
    /// is used (by the result event). Delegates to the richer public <see cref="Consume"/>.</summary>
    IEnumerable<AgentStreamOutput> IAgentStreamParser.Parse(Guid agentTaskId, JsonElement frame, bool isInterrupted)
        => Consume(frame, isInterrupted);

    /// <summary>
    /// Handles one parsed stream-json event. <paramref name="isInterrupted"/> is the live
    /// <c>AgentProcessContext.IsInterrupted</c> read by the pump (only the result
    /// event uses it — an interrupt is process state the parser can't observe). A scalar, not
    /// the context: the parser stays a pure <c>frame → data</c> function with no handle/ctx access.
    /// </summary>
    public IEnumerable<AgentStreamOutput> Consume(JsonElement root, bool isInterrupted)
    {
        if (!root.TryGetProperty("type", out var typeProp))
            return [];

        var parentToolUseId = root.TryGetProperty("parent_tool_use_id", out var ptProp)
            && ptProp.ValueKind == JsonValueKind.String
            ? ptProp.GetString()
            : null;

        return typeProp.GetString() switch
        {
            "system" => HandleSystem(root),
            "assistant" => HandleAssistant(root, parentToolUseId),
            "user" => HandleUser(root, parentToolUseId),
            "result" => HandleResult(root, isInterrupted),
            "error" => HandleError(root),
            "stream_event" => HandleStreamEvent(root, parentToolUseId),
            "rate_limit_event" => HandleRateLimit(root),
            "control_request" => HandleControlRequest(root),
            "control_response" => HandleControlResponse(root),
            _ => [],
        };
    }

    // ── bidirectional frames (pure data; the dispatch acts on these with the handle) ──

    /// <summary>
    /// Builds an <see cref="InteractionRequested"/> from a <c>control_request/can_use_tool</c>
    /// frame — the permission prompt / AskUserQuestion the CLI blocks on. Pure data: the reply is
    /// written later by the dispatch via the keyed reply builder, so this never touches the handle.
    /// </summary>
    private IEnumerable<AgentStreamOutput> HandleControlRequest(JsonElement root)
    {
        if (!root.TryGetProperty("request_id", out var requestIdProp))
            yield break;
        var requestId = requestIdProp.GetString();
        if (string.IsNullOrEmpty(requestId))
            yield break;

        if (!root.TryGetProperty("request", out var request)
            || !request.TryGetProperty("subtype", out var subtypeProp))
            yield break;

        var subtype = subtypeProp.GetString();
        _logger.LogDebug("Received control_request subtype={Subtype}, request_id={RequestId}", subtype, requestId);

        if (subtype != "can_use_tool")
            yield break;

        var toolName = request.TryGetProperty("tool_name", out var tn) ? tn.GetString() : null;
        var toolInputRaw = request.TryGetProperty("input", out var ti) ? ti.GetRawText() : null;

        var isAskUser = string.Equals(toolName, "AskUserQuestion", StringComparison.OrdinalIgnoreCase);
        var isExitPlanMode = string.Equals(toolName, "ExitPlanMode", StringComparison.OrdinalIgnoreCase);

        string? suggestionsJson = null;
        if (!isAskUser && request.TryGetProperty("permission_suggestions", out var suggestionsEl)
            && suggestionsEl.ValueKind == JsonValueKind.Array)
        {
            suggestionsJson = suggestionsEl.GetRawText();
        }

        string? decisionReason = null;
        if (request.TryGetProperty("decision_reason", out var reasonEl)
            && reasonEl.ValueKind == JsonValueKind.String)
        {
            decisionReason = reasonEl.GetString();
        }

        string? planContent = null;
        if (isExitPlanMode && toolInputRaw is not null)
        {
            try
            {
                using var planDoc = JsonDocument.Parse(toolInputRaw);
                var planRoot = planDoc.RootElement;
                planContent = planRoot.TryGetProperty("plan", out var planProp) ? planProp.GetString()
                    : planRoot.TryGetProperty("content", out var contentProp) ? contentProp.GetString()
                    : planRoot.TryGetProperty("planContent", out var pcProp) ? pcProp.GetString()
                    : null;
            }
            catch { }
        }

        string? questionsJson = null;
        string? firstQuestionText = null;
        if (isAskUser && toolInputRaw is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(toolInputRaw);
                if (doc.RootElement.TryGetProperty("questions", out var qArr)
                    && qArr.ValueKind == JsonValueKind.Array)
                {
                    questionsJson = qArr.GetRawText();
                    if (qArr.GetArrayLength() > 0
                        && qArr[0].TryGetProperty("question", out var qText))
                    {
                        firstQuestionText = qText.GetString();
                    }
                }
            }
            catch { }
        }

        var message = new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = _agentTaskId,
            Role = MessageRole.System,
            Type = isAskUser
                ? MessageType.UserQuestion
                : MessageType.PermissionRequest,
            Content = isAskUser ? firstQuestionText
                : isExitPlanMode ? planContent
                : null,
            Status = MessageStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
            UserInteraction = new UserInteractionData
            {
                Id = Guid.NewGuid(),
                RequestId = requestId,
                ToolName = toolName,
                ToolInput = toolInputRaw,
                Reason = decisionReason,
                Questions = questionsJson,
                Suggestions = suggestionsJson,
            },
        };

        yield return new InteractionRequested(
            requestId, message,
            CacheKey: null, // Claude has no MCP session cache
            NotifyContent: message.Content, NotifyToolName: toolName, NotifyCommand: null);
    }

    /// <summary>Maps a <c>control_response</c> frame to a <see cref="ControlResponseReceived"/>
    /// so the dispatch can route it to its pending waiter.</summary>
    private static IEnumerable<AgentStreamOutput> HandleControlResponse(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response)
            && response.TryGetProperty("request_id", out var idProp)
            && idProp.GetString() is { Length: > 0 } id)
        {
            yield return new ControlResponseReceived(id, root);
        }
    }

    // ── system ──

    private IEnumerable<AgentStreamOutput> HandleSystem(JsonElement root)
    {
        var subtype = root.TryGetProperty("subtype", out var subtypeProp) ? subtypeProp.GetString() : null;

        if (subtype == "compact_boundary")
            return HandleCompactBoundary(root);

        if (subtype == "status")
            return HandleStatus(root);

        if (root.TryGetProperty("session_id", out var sessionIdProp)
            && sessionIdProp.GetString() is { Length: > 0 } sessionId)
            return [new SessionIdChanged(sessionId)];

        return [];
    }

    /// <summary>
    /// <c>subtype:"status"</c> carries compaction lifecycle: <c>status:"compacting"</c> →
    /// banner on; anything with <c>compact_result</c> → banner off (covers success + error).
    /// </summary>
    private static IEnumerable<AgentStreamOutput> HandleStatus(JsonElement root)
    {
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        var hasCompactResult = root.TryGetProperty("compact_result", out _);

        if (status == "compacting")
            return [new CompactingChanged(Active: true)];
        if (hasCompactResult)
            return [new CompactingChanged(Active: false)];
        return [];
    }

    /// <summary>
    /// Stash the boundary and wait for the synthetic summary user event that Claude emits
    /// right after, so the two publish as one atomic row. Flushes any previous unflushed
    /// boundary first (defensive).
    /// </summary>
    private IEnumerable<AgentStreamOutput> HandleCompactBoundary(JsonElement root)
    {
        foreach (var o in FlushPendingCompactBoundary())
            yield return o;

        var message = ClaudeCodeOutputParser.ParseCompactBoundaryEvent(_agentTaskId, root);

        if (_pendingManualCompact)
        {
            message.CompactBoundary!.Trigger = CompactTrigger.Manual;
            _pendingManualCompact = false;
        }

        _pendingCompactBoundary = message;

        _logger.LogInformation(
            "Compact boundary for AgentTask {TaskId}: trigger={Trigger} pre={Pre} post={Post} duration={Duration}ms — waiting for synthetic summary",
            _agentTaskId, message.CompactBoundary!.Trigger,
            message.CompactBoundary.PreTokens, message.CompactBoundary.PostTokens, message.CompactBoundary.DurationMs);
    }

    /// <summary>Publish a stashed boundary without a summary (defensive — used before the next
    /// assistant/result event and if the synthetic summary never arrives).</summary>
    private IEnumerable<AgentStreamOutput> FlushPendingCompactBoundary()
    {
        if (_pendingCompactBoundary is null)
            yield break;

        var pending = _pendingCompactBoundary;
        _pendingCompactBoundary = null;
        yield return new MessageOutput(pending);
        yield return new CompactingChanged(Active: false);
        _logger.LogWarning(
            "Flushed pending compact boundary without synthetic summary for AgentTask {TaskId}", _agentTaskId);
    }

    // ── assistant ──

    private IEnumerable<AgentStreamOutput> HandleAssistant(JsonElement root, string? parentToolUseId)
    {
        // Defensive fallback: emit any dangling boundary before the next assistant turn.
        foreach (var o in FlushPendingCompactBoundary())
            yield return o;

        // Capture an upstream API error reported on the assistant event so the result event
        // can classify the turn outcome if it ends in failure.
        CaptureAssistantApiError(root);

        if (root.TryGetProperty("uuid", out var uuidProp)
            && uuidProp.GetString() is { Length: > 0 } assistantUuid)
            yield return new ExternalMessageIdAssigned(MessageRole.Assistant, assistantUuid);

        foreach (var msg in ClaudeCodeOutputParser.ParseAssistantEvent(
                     _agentTaskId, root, _toolUseRegistry, _logger, parentToolUseId))
            yield return new MessageOutput(msg);

        var usage = BuildContextUsageFromAssistantEvent(root);
        if (usage is not null)
            yield return new DeltaOutput(usage);

        // The streaming fragments are now consolidated into persisted messages — drop them
        // from the delta snapshot so reconnection replay stays bounded.
        yield return new ClearDeltaSnapshot();
    }

    // ── user ──

    private IEnumerable<AgentStreamOutput> HandleUser(JsonElement root, string? parentToolUseId)
    {
        // Synthetic summary right after a compact_boundary — attach + publish the combined row.
        if (_pendingCompactBoundary is not null)
        {
            var summary = ClaudeCodeOutputParser.ExtractCompactSummaryFromSyntheticUserEvent(root);
            if (summary is not null)
            {
                var pending = _pendingCompactBoundary;
                if (pending.CompactBoundary is not null)
                    pending.CompactBoundary.SummaryText = summary;
                _pendingCompactBoundary = null;

                yield return new MessageOutput(pending);
                yield return new CompactingChanged(Active: false);
                _logger.LogInformation(
                    "Published compact boundary with summary ({Len} chars) for AgentTask {TaskId}",
                    summary.Length, _agentTaskId);
                yield break;
            }
        }

        if (root.TryGetProperty("uuid", out var uuidProp)
            && uuidProp.GetString() is { Length: > 0 } uuid)
            yield return new ExternalMessageIdAssigned(MessageRole.User, uuid);

        foreach (var msg in ClaudeCodeOutputParser.ParseUserEvent(
                     _agentTaskId, root, _toolUseRegistry, _logger, parentToolUseId))
            yield return new MessageOutput(msg);
    }

    // ── result (turn end) ──

    private IEnumerable<AgentStreamOutput> HandleResult(JsonElement root, bool isInterrupted)
    {
        _logger.LogInformation("Turn completed for AgentTask {TaskId}", _agentTaskId);

        // Flush a dangling boundary so it lands before the turn boundary.
        foreach (var o in FlushPendingCompactBoundary())
            yield return o;

        if (root.TryGetProperty("session_id", out var sessionIdProp)
            && sessionIdProp.GetString() is { Length: > 0 } sessionId)
            yield return new SessionIdChanged(sessionId);

        if (isInterrupted)
        {
            // Persist any partial content from the interrupted streaming turn, then note it.
            yield return new FlushDeltaSnapshot();
            yield return new MessageOutput(SystemMessage(
                "Turn interrupted by user", MessageType.Other, MessageStatus.Completed));
        }

        var usage = BuildContextUsageFromResultEvent(root);
        if (usage is not null)
            yield return new DeltaOutput(usage);

        var turnResult = root.Clone();

        // An interrupted turn is a user cancel, not a failure. Otherwise inspect the result
        // plus any upstream error captured during the turn.
        var failure = isInterrupted ? null : ClassifyClaudeFailure(root, _pendingTurnFailure);
        _pendingTurnFailure = null;

        if (failure is not null)
            _logger.LogWarning("Claude turn failed for AgentTask {TaskId}: kind={Kind}, message={Message}",
                _agentTaskId, failure.Kind, failure.Message);

        yield return new TurnEnded(turnResult, isInterrupted, failure);
    }

    // ── stream_event ──

    private IEnumerable<AgentStreamOutput> HandleStreamEvent(JsonElement root, string? parentToolUseId)
    {
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root, parentToolUseId);
        if (payload is null)
            yield break;

        yield return new DeltaOutput(payload);

        if (payload is TurnPayload { IsStart: true })
            yield return new TurnStarted();
    }

    // ── rate_limit_event ──

    private IEnumerable<AgentStreamOutput> HandleRateLimit(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info))
            yield break;

        var status = info.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
        if (status is not ("allowed_warning" or "rejected"))
            yield break;

        var utilization = info.TryGetProperty("utilization", out var utilProp)
            && utilProp.ValueKind == JsonValueKind.Number
            ? utilProp.GetDouble()
            : (double?)null;

        var rateLimitType = info.TryGetProperty("rateLimitType", out var typeProp)
            ? typeProp.GetString()
            : null;

        var content = status switch
        {
            "allowed_warning" => utilization.HasValue
                ? $"Rate limit warning: {utilization.Value:P0} of {rateLimitType ?? "rate"} limit used"
                : "Rate limit warning: approaching API rate limit",
            "rejected" => "Rate limited: request rejected — rate limit reached",
            _ => $"Rate limit event: {status}",
        };

        _logger.LogWarning("Rate limit event for AgentTask {TaskId}: status={Status}, utilization={Util}, type={Type}",
            _agentTaskId, status, utilization, rateLimitType);

        // A rejection blocks the turn — remember it so the result event reports a rate-limit
        // failure, not "completed". A warning is informational (the turn proceeds).
        if (status == "rejected")
            _pendingTurnFailure = new TurnFailure(TurnFailureKind.RateLimited, content);

        yield return new MessageOutput(SystemMessage(
            content, MessageType.Other, MessageStatus.Completed));
    }

    // ── error ──

    private IEnumerable<AgentStreamOutput> HandleError(JsonElement root)
    {
        string? errorMessage = null;
        if (root.TryGetProperty("error", out var errorObj)
            && errorObj.TryGetProperty("message", out var msgProp))
            errorMessage = msgProp.GetString();

        _logger.LogWarning("Claude Code error for AgentTask {TaskId}: {Error}", _agentTaskId, errorMessage);

        _pendingTurnFailure = TurnFailure.FromText(errorMessage, TurnFailureKind.ApiError);
        return [];
    }

    // ── context usage ──

    /// <summary>
    /// Per-message context footprint from a streamed <c>assistant</c> event's
    /// <c>message.usage</c>. Skips sub-agent events (non-null parent_tool_use_id) — they run
    /// in their own window. Caches the parent model so the result event can pick the matching
    /// <c>modelUsage</c> entry.
    /// </summary>
    private ContextUsagePayload? BuildContextUsageFromAssistantEvent(JsonElement root)
    {
        if (root.TryGetProperty("parent_tool_use_id", out var parentToolUseId)
            && parentToolUseId.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(parentToolUseId.GetString()))
            return null;

        if (!root.TryGetProperty("message", out var message)) return null;
        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

        var used = SumInputTokens(usage);
        if (used <= 0) return null;

        var model = message.TryGetProperty("model", out var modelProp)
            && modelProp.ValueKind == JsonValueKind.String
                ? modelProp.GetString()
                : null;

        if (!string.IsNullOrEmpty(model))
            _lastParentModel = model;

        return new ContextUsagePayload(used, WindowTokens: null, Model: model, CostUsd: null);
    }

    /// <summary>
    /// Tops up from the CLI's <c>result</c> event with <c>modelUsage[model].contextWindow</c>
    /// and running <c>total_cost_usd</c> — the fields the per-message emit can't know. Does NOT
    /// touch "used" tokens (modelUsage totals are session-cumulative). Picks the parent's entry
    /// via <see cref="_lastParentModel"/> (exact or <c>"name[variant]"</c> match), falling back
    /// to the first key.
    /// </summary>
    private ContextUsagePayload? BuildContextUsageFromResultEvent(JsonElement root)
    {
        long? window = null;
        string? model = null;
        double? cost = null;

        if (root.TryGetProperty("modelUsage", out var modelUsage)
            && modelUsage.ValueKind == JsonValueKind.Object)
        {
            var parentHint = _lastParentModel;
            JsonElement? chosenValue = null;
            string? chosenKey = null;
            JsonElement? firstValue = null;
            string? firstKey = null;

            foreach (var entry in modelUsage.EnumerateObject())
            {
                firstKey ??= entry.Name;
                firstValue ??= entry.Value;

                if (parentHint is not null && IsParentModelMatch(entry.Name, parentHint))
                {
                    chosenKey = entry.Name;
                    chosenValue = entry.Value;
                    break;
                }
            }

            chosenKey ??= firstKey;
            chosenValue ??= firstValue;

            if (chosenKey is not null && chosenValue is { ValueKind: JsonValueKind.Object } cv)
            {
                model = chosenKey;
                if (cv.TryGetProperty("contextWindow", out var w)
                    && w.ValueKind == JsonValueKind.Number)
                    window = w.GetInt64();
            }
        }

        if (root.TryGetProperty("total_cost_usd", out var costProp)
            && costProp.ValueKind == JsonValueKind.Number)
            cost = costProp.GetDouble();

        if (window is null && model is null && cost is null) return null;

        return new ContextUsagePayload(UsedTokens: null, window, model, cost);
    }

    private static bool IsParentModelMatch(string modelUsageKey, string parentModelHint)
    {
        if (modelUsageKey == parentModelHint) return true;
        return modelUsageKey.Length > parentModelHint.Length
            && modelUsageKey.StartsWith(parentModelHint, StringComparison.Ordinal)
            && modelUsageKey[parentModelHint.Length] == '[';
    }

    private static long SumInputTokens(JsonElement usage)
    {
        long total = 0;
        if (usage.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number)
            total += i.GetInt64();
        if (usage.TryGetProperty("cache_read_input_tokens", out var r) && r.ValueKind == JsonValueKind.Number)
            total += r.GetInt64();
        if (usage.TryGetProperty("cache_creation_input_tokens", out var c) && c.ValueKind == JsonValueKind.Number)
            total += c.GetInt64();
        return total;
    }

    // ── failure classification ──

    /// <summary>
    /// Whether a <c>result</c> event is a failed turn and, if so, why. Failed when
    /// <c>is_error</c> is true or <c>subtype</c> starts with "error". A precise upstream signal
    /// captured mid-turn wins over the coarse result subtype.
    /// </summary>
    private static TurnFailure? ClassifyClaudeFailure(JsonElement root, TurnFailure? pending)
    {
        var isError = root.TryGetProperty("is_error", out var ie)
            && (ie.ValueKind == JsonValueKind.True
                || (ie.ValueKind == JsonValueKind.String && string.Equals(ie.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

        var subtype = root.TryGetProperty("subtype", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString()
            : null;

        var resultText = root.TryGetProperty("result", out var rt) && rt.ValueKind == JsonValueKind.String
            ? rt.GetString()
            : null;

        var failed = isError
            || (subtype is not null && subtype.StartsWith("error", StringComparison.OrdinalIgnoreCase));
        if (!failed)
            return null;

        if (pending is not null)
            return pending.Message is null && !string.IsNullOrWhiteSpace(resultText)
                ? pending with { Message = resultText }
                : pending;

        TurnFailureKind kind;
        if (subtype is not null && subtype.Contains("max_turns", StringComparison.OrdinalIgnoreCase))
            kind = TurnFailureKind.MaxTurns;
        else if (subtype is not null && subtype.Contains("max_budget", StringComparison.OrdinalIgnoreCase))
            kind = TurnFailureKind.ApiError;
        else
            kind = TurnFailure.ClassifyFromText(resultText);

        return new TurnFailure(kind, resultText ?? HumanizeClaudeSubtype(subtype));
    }

    private static string HumanizeClaudeSubtype(string? subtype) => subtype switch
    {
        "error_max_turns" => "The turn hit the configured max-turns limit.",
        "error_max_budget_usd" => "The turn hit the configured budget limit.",
        "error_during_execution" => "The agent hit an error during execution.",
        null => "The turn ended with an error.",
        _ => $"The turn ended with an error ({subtype}).",
    };

    /// <summary>
    /// Captures an upstream API error reported on an <c>assistant</c> event's <c>error</c>
    /// field (rate_limit, authentication_failed, server_error, …) so the result handler can
    /// name the cause if the turn ends in failure.
    /// </summary>
    private void CaptureAssistantApiError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var errorProp)
            || errorProp.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        string? subtype = null;
        string? message = null;

        if (errorProp.ValueKind == JsonValueKind.String)
        {
            subtype = errorProp.GetString();
        }
        else if (errorProp.ValueKind == JsonValueKind.Object)
        {
            if (errorProp.TryGetProperty("subtype", out var s) && s.ValueKind == JsonValueKind.String)
                subtype = s.GetString();
            else if (errorProp.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String)
                subtype = ty.GetString();

            if (errorProp.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                message = m.GetString();
        }

        var kind = MapAssistantErrorSubtype(subtype);
        if (kind == TurnFailureKind.Unknown)
            kind = TurnFailure.ClassifyFromText(message ?? subtype);

        _pendingTurnFailure = new TurnFailure(kind, message ?? subtype);
    }

    private static TurnFailureKind MapAssistantErrorSubtype(string? subtype) => subtype switch
    {
        "rate_limit" => TurnFailureKind.RateLimited,
        "overloaded_error" => TurnFailureKind.Overloaded,
        "authentication_failed" => TurnFailureKind.Auth,
        "billing_error" => TurnFailureKind.Auth,
        "invalid_request" => TurnFailureKind.ApiError,
        "server_error" => TurnFailureKind.ApiError,
        _ => TurnFailureKind.Unknown,
    };

    private AgentMessage SystemMessage(string? content, MessageType type, MessageStatus status)
        => new()
        {
            Id = Guid.NewGuid(),
            AgentTaskId = _agentTaskId,
            Role = MessageRole.System,
            Type = type,
            Content = content,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };
}
