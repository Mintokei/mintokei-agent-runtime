using System.Text.Json;

using Mintokei.AgentEngine.Contracts;

using Mintokei.AgentEngine.AgentTools.Codex;

namespace Mintokei.AgentEngine.Codex;

/// <summary>
/// Parses Codex app-server JSON-RPC frames into the backend-agnostic
/// <see cref="AgentStreamOutput"/> vocabulary — both the one-way notifications
/// (<c>item/started</c>, <c>item/completed</c>, <c>error</c>, <c>turn/completed</c>,
/// <c>thread/tokenUsageUpdated</c>) and the bidirectional frames as <em>pure data</em>:
/// a response (id, no method) → <see cref="ControlResponseReceived"/>, and a server-originated
/// request (command / file-change approval, user-input prompt, MCP elicitation) →
/// <see cref="InteractionRequested"/> (with a <c>CacheKey</c> for elicitation). The service
/// dispatch acts on the bidirectional cases with the process handle; the parser never touches it.
///
/// Per-task and stateful: it remembers the upstream error captured mid-turn (so the
/// following <c>turn/completed</c> is classified as a failure) and whether the in-flight
/// compaction was user-initiated (so the boundary row is tagged Manual vs Auto). These
/// were previously <c>ctx.PendingTurnFailure</c> / <c>ctx.PendingManualCompact</c> and the
/// pump-local <c>errorPublished</c> flag.
/// </summary>
internal sealed class CodexStreamParser : IAgentStreamParser
{
    private readonly ILogger _logger;
    private readonly Guid _agentTaskId;

    /// <summary>Upstream error captured from an <c>error</c> notification, consumed by the
    /// next <c>turn/completed</c> so a failed turn surfaces why it failed.</summary>
    private TurnFailure? _pendingTurnFailure;

    /// <summary>Whether an <c>error</c> notification already published a user-visible error
    /// message this turn — suppresses the turn/completed fallback error row.</summary>
    private bool _errorPublished;

    /// <summary>Set by the service when the user triggers <c>/compact</c>; consumed by the
    /// next <c>contextCompaction</c> item so its boundary is tagged Manual.</summary>
    private bool _pendingManualCompact;

    public CodexStreamParser(ILogger logger, Guid agentTaskId)
    {
        _logger = logger;
        _agentTaskId = agentTaskId;
    }

    /// <summary>Marks the next contextCompaction boundary as user-initiated.</summary>
    public void NoteManualCompact() => _pendingManualCompact = true;

    /// <summary>Pump entry point (<see cref="IAgentStreamParser"/>). The parser carries its own
    /// task id and Codex has no interrupt-flag dependency, so both parameters are ignored.
    /// Delegates to the richer public <see cref="Consume"/>.</summary>
    IEnumerable<AgentStreamOutput> IAgentStreamParser.Parse(Guid agentTaskId, JsonElement frame, bool isInterrupted)
        => Consume(frame);

    /// <summary>
    /// Handles one JSON-RPC notification envelope (id-less). Returns zero or more outputs.
    /// The caller has already filtered out responses and server-originated requests.
    /// </summary>
    public IEnumerable<AgentStreamOutput> Consume(JsonElement msg)
    {
        var hasId = msg.TryGetProperty("id", out var idProp);
        var method = msg.TryGetProperty("method", out var m) ? m.GetString() : null;

        // JSON-RPC response (id, no method) → route it to its pending waiter.
        if (hasId && method is null)
        {
            var id = idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt32().ToString()
                : idProp.GetString();
            return id is { Length: > 0 } ? [new ControlResponseReceived(id, msg)] : [];
        }

        // Server-originated request (id + method) → a permission/question the CLI blocks on.
        if (hasId && method is not null)
        {
            var rpcId = idProp.GetRawText();
            return method switch
            {
                "item/commandExecution/requestApproval" or "item/fileChange/requestApproval"
                    => BuildApprovalRequest(rpcId, method, msg),
                "item/tool/requestUserInput" => BuildUserInputRequest(rpcId, msg),
                "mcpServer/elicitation/request" => BuildElicitationRequest(rpcId, msg),
                _ => [],
            };
        }

        // JSON-RPC notification (no id) → one-way transcript.
        return method switch
        {
            "item/started" => HandleItemStarted(msg),
            "item/completed" => HandleItemCompleted(msg),
            "error" => HandleError(msg),
            "turn/completed" => HandleTurnCompleted(msg),
            "thread/tokenUsageUpdated" => HandleTokenUsageUpdated(msg),
            "codex/event/error" or "codex/event/warning" => LogAndIgnore(method, msg),
            _ => [],
        };
    }

    private IEnumerable<AgentStreamOutput> LogAndIgnore(string method, JsonElement msg)
    {
        _logger.LogWarning("[codex {Method}] Full JSON: {Json}", method, msg.GetRawText());
        return [];
    }

    // ── server-originated requests → InteractionRequested (pure data; the dispatch replies) ──

    private IEnumerable<AgentStreamOutput> BuildApprovalRequest(string rpcId, string methodName, JsonElement msg)
    {
        var @params = msg.TryGetProperty("params", out var p) ? p : default;
        var command = @params.TryGetProperty("command", out var cmd) ? cmd.GetString() : null;
        var cwd = @params.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
        var reason = @params.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null;

        var isFileChange = methodName == "item/fileChange/requestApproval";
        var message = new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = _agentTaskId,
            Role = MessageRole.System,
            Type = MessageType.PermissionRequest,
            Content = reason ?? (isFileChange ? "File change approval requested" : command),
            Status = MessageStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
            UserInteraction = new UserInteractionData
            {
                Id = Guid.NewGuid(),
                RequestId = rpcId,
                Command = command,
                Cwd = cwd,
                Reason = reason,
                ReplyContext = CodexInteractionReplyBuilder.ApprovalContext,
            },
        };

        return [new InteractionRequested(
            rpcId, message,
            CacheKey: null,
            NotifyContent: message.Content,
            NotifyToolName: isFileChange ? "FileChange" : "Shell", NotifyCommand: command)];
    }

    private IEnumerable<AgentStreamOutput> BuildUserInputRequest(string rpcId, JsonElement msg)
    {
        var @params = msg.TryGetProperty("params", out var p) ? p : default;
        var questionsJson = @params.TryGetProperty("questions", out var q) ? q.GetRawText() : null;

        var message = new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = _agentTaskId,
            Role = MessageRole.System,
            Type = MessageType.UserQuestion,
            Content = null,
            Status = MessageStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
            UserInteraction = new UserInteractionData
            {
                Id = Guid.NewGuid(),
                RequestId = rpcId,
                Questions = questionsJson,
                ReplyContext = CodexInteractionReplyBuilder.UserInputContext,
            },
        };

        return [new InteractionRequested(
            rpcId, message,
            CacheKey: null,
            NotifyContent: ExtractFirstQuestionText(questionsJson), NotifyToolName: null, NotifyCommand: null)];
    }

    private IEnumerable<AgentStreamOutput> BuildElicitationRequest(string rpcId, JsonElement msg)
    {
        var @params = msg.TryGetProperty("params", out var p) ? p : default;
        var serverName = @params.TryGetProperty("serverName", out var sn) ? sn.GetString() : null;
        var question = @params.TryGetProperty("message", out var m) ? m.GetString() : null;

        string? toolDescription = null;
        string? toolParamsJson = null;
        var offersSessionPersist = false;
        if (@params.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (meta.TryGetProperty("tool_description", out var td) && td.ValueKind == JsonValueKind.String)
                toolDescription = td.GetString();
            if (meta.TryGetProperty("tool_params", out var tp)
                && tp.ValueKind != JsonValueKind.Null
                && tp.ValueKind != JsonValueKind.Undefined)
                toolParamsJson = tp.GetRawText();
            offersSessionPersist = MetaOffersSessionPersist(meta);
        }

        var toolName = ExtractToolNameFromMessage(question);
        var cacheKey = serverName is not null && toolName is not null
            ? $"{serverName}:{toolName}"
            : null;

        // The cache read (auto-accept) and the "allow for session" write live in the dispatch,
        // keyed off CacheKey — the parser only reports the key.
        string? suggestionsJson = null;
        if (offersSessionPersist && cacheKey is not null)
        {
            suggestionsJson = JsonSerializer.Serialize(
                new object[] { new { type = "mcpSessionScope" } },
                CodexJsonRpcHelper.JsonOptions);
        }

        var message = new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = _agentTaskId,
            Role = MessageRole.System,
            Type = MessageType.PermissionRequest,
            Content = question,
            Status = MessageStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
            UserInteraction = new UserInteractionData
            {
                Id = Guid.NewGuid(),
                RequestId = rpcId,
                ToolName = serverName,
                ToolInput = toolParamsJson,
                Reason = toolDescription,
                Suggestions = suggestionsJson,
                ReplyContext = CodexInteractionReplyBuilder.ElicitationContext,
            },
        };

        return [new InteractionRequested(
            rpcId, message,
            CacheKey: cacheKey,
            NotifyContent: message.Content ?? toolDescription,
            NotifyToolName: serverName is not null && toolName is not null ? $"{serverName}/{toolName}" : serverName ?? toolName,
            NotifyCommand: null)];
    }

    private static bool MetaOffersSessionPersist(JsonElement meta)
    {
        if (!meta.TryGetProperty("persist", out var persist))
            return false;

        if (persist.ValueKind == JsonValueKind.String)
            return persist.GetString() == "session";

        if (persist.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in persist.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() == "session")
                    return true;
        }

        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex ToolNameRegex =
        new("run tool \"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? ExtractToolNameFromMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var match = ToolNameRegex.Match(message);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractFirstQuestionText(string? questionsJson)
    {
        if (string.IsNullOrEmpty(questionsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(questionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;
            var first = doc.RootElement[0];
            return first.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.String
                ? q.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// item/started fires the moment Codex begins processing an item. We only care about
    /// <c>contextCompaction</c> starts — so the UI can show a persistent "Compacting…"
    /// banner before the operation completes. Every other item type is ignored.
    /// </summary>
    private IEnumerable<AgentStreamOutput> HandleItemStarted(JsonElement msg)
    {
        if (msg.TryGetProperty("params", out var @params)
            && @params.TryGetProperty("item", out var item)
            && item.TryGetProperty("type", out var typeProp)
            && typeProp.GetString() == "contextCompaction")
        {
            yield return new CompactingChanged(Active: true);
        }
    }

    private IEnumerable<AgentStreamOutput> HandleItemCompleted(JsonElement msg)
    {
        if (!msg.TryGetProperty("params", out var @params)
            || !@params.TryGetProperty("item", out var item))
        {
            _logger.LogDebug("item/completed notification missing params.item, skipping");
            yield break;
        }

        // Codex represents compaction as a contextCompaction thread item. The item itself
        // only carries {id, type} — no summary, no tokens (Codex keeps the summary encrypted
        // in the rollout). We publish a CompactBoundary-typed row so the UI still gets a
        // boundary marker with trigger + completion.
        if (item.TryGetProperty("type", out var typeProp)
            && typeProp.GetString() == "contextCompaction")
        {
            var message = CodexThreadItemParser.ParseContextCompaction(_agentTaskId, item);

            if (_pendingManualCompact)
            {
                message.CompactBoundary!.Trigger = CompactTrigger.Manual;
                _pendingManualCompact = false;
            }

            CodexStreamParserLog.CompactBoundary(_logger, _agentTaskId, message.CompactBoundary!.Trigger);

            yield return new MessageOutput(message);
            // Compaction complete → drop the banner. Per-turn token counts arrive separately
            // on thread/tokenUsageUpdated; the contextCompaction item carries no summary text.
            yield return new CompactingChanged(Active: false);
            yield break;
        }

        var parsed = CodexThreadItemParser.Parse(_agentTaskId, item, _logger);
        if (parsed is not null)
            yield return new MessageOutput(parsed);
    }

    private IEnumerable<AgentStreamOutput> HandleError(JsonElement msg)
    {
        var @params = msg.TryGetProperty("params", out var p) ? p : default;

        var willRetry = @params.ValueKind != JsonValueKind.Undefined
                        && @params.TryGetProperty("willRetry", out var wr)
                        && wr.GetBoolean();

        if (willRetry)
        {
            _logger.LogWarning("Codex error (will retry) for AgentTask {TaskId}: {Msg}",
                _agentTaskId, @params.GetRawText());
            yield break;
        }

        var errorElement = @params.TryGetProperty("error", out var err) ? err : @params;
        var errorMessage = ExtractErrorMessage(errorElement);
        var codexErrorInfo = ExtractCodexErrorInfo(errorElement);

        _logger.LogError("Codex error for AgentTask {TaskId}: extracted message={Error}, info={Info}",
            _agentTaskId, errorMessage, codexErrorInfo);

        // Remember the error so the turn/completed that follows is reported as a failure
        // (with this reason) rather than a successful completion.
        _errorPublished = true;
        _pendingTurnFailure = TurnFailure.FromText(errorMessage, TurnFailureKind.ApiError);

        yield return new MessageOutput(new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = _agentTaskId,
            Role = MessageRole.System,
            Type = MessageType.Error,
            Content = errorMessage,
            Status = MessageStatus.Failed,
            Metadata = codexErrorInfo,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private IEnumerable<AgentStreamOutput> HandleTurnCompleted(JsonElement msg)
    {
        CodexStreamParserLog.TurnCompleted(_logger, _agentTaskId);

        JsonElement? turnResult = msg.TryGetProperty("params", out var p) ? p.Clone() : null;

        // Pull the turn element + status once; every branch keys off it.
        JsonElement? turn = null;
        string? status = null;
        if (turnResult is { } tr && tr.TryGetProperty("turn", out var turnEl))
        {
            turn = turnEl;
            if (turnEl.TryGetProperty("status", out var statusEl))
                status = statusEl.GetString();
        }

        var isInterrupted = status == "interrupted";
        var isFailed = status == "failed";

        // Capture then reset the per-turn signals so a stale one can't leak into the next turn.
        var failure = _pendingTurnFailure;
        var errorAlreadyPublished = _errorPublished;
        _pendingTurnFailure = null;
        _errorPublished = false;

        if (isInterrupted)
        {
            yield return new MessageOutput(SystemMessage(
                "Turn interrupted by user", MessageType.Other, MessageStatus.Completed));
        }

        // A failed turn with no prior error notification gets a fallback error row.
        if (!errorAlreadyPublished && isFailed)
        {
            var errorText = turn is { } t && t.TryGetProperty("error", out var errorObj)
                ? ExtractErrorMessage(errorObj)
                : "Turn failed";

            yield return new MessageOutput(SystemMessage(
                errorText, MessageType.Error, MessageStatus.Failed));

            failure ??= TurnFailure.FromText(errorText, TurnFailureKind.ApiError);
        }

        // A turn.status == "failed" reported even when an error notification already
        // published is still a failure — fall back to a generic classification.
        if (failure is null && isFailed)
        {
            var errorText = turn is { } t2 && t2.TryGetProperty("error", out var errObj)
                ? ExtractErrorMessage(errObj)
                : "Turn failed";
            failure = TurnFailure.FromText(errorText, TurnFailureKind.ApiError);
        }

        // An interrupt is a user cancel, not a failure.
        if (isInterrupted)
            failure = null;

        if (failure is not null)
            _logger.LogWarning("Codex turn failed for AgentTask {TaskId}: kind={Kind}, message={Message}",
                _agentTaskId, failure.Kind, failure.Message);

        yield return new TurnEnded(turnResult, isInterrupted, failure);
    }

    /// <summary>
    /// Codex emits <c>thread/tokenUsageUpdated</c> after every turn with both the last-turn
    /// breakdown and a running total, plus the model's context-window size. Mirror it onto
    /// the SSE delta channel as a <see cref="ContextUsagePayload"/> so the UI gauge updates
    /// without waiting for the next full message. (The dispatcher also persists it.)
    /// </summary>
    private IEnumerable<AgentStreamOutput> HandleTokenUsageUpdated(JsonElement msg)
    {
        if (!msg.TryGetProperty("params", out var @params)
            || !@params.TryGetProperty("tokenUsage", out var tokenUsage)
            || tokenUsage.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        long? window = null;
        if (tokenUsage.TryGetProperty("modelContextWindow", out var w)
            && w.ValueKind == JsonValueKind.Number)
            window = w.GetInt64();

        // Use the most recent turn's input footprint — that's "what's currently in context"
        // rather than the cumulative thread total which keeps growing.
        var used = tokenUsage.TryGetProperty("last", out var last)
            && last.ValueKind == JsonValueKind.Object
                ? ExtractInputTokens(last)
                : 0;

        if (used <= 0 && window is null)
            yield break;

        long? usedOpt = used > 0 ? used : null;
        yield return new DeltaOutput(new ContextUsagePayload(usedOpt, window, Model: null, CostUsd: null));
    }

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

    /// <summary>Input footprint from a Codex <c>TokenUsageBreakdown</c>. Per
    /// <c>codex-rs/protocol/src/protocol.rs</c>, <c>cachedInputTokens</c> is a
    /// <em>subset</em> of <c>inputTokens</c> (the upstream <c>non_cached_input()</c>
    /// computes <c>input - cached</c>), so <c>inputTokens</c> already covers both cached
    /// and uncached — don't add <c>cachedInputTokens</c> on top or you double-count.</summary>
    private static long ExtractInputTokens(JsonElement breakdown)
    {
        if (breakdown.TryGetProperty("inputTokens", out var i) && i.ValueKind == JsonValueKind.Number)
            return i.GetInt64();
        return 0;
    }

    private static string ExtractErrorMessage(JsonElement @params)
    {
        if (@params.TryGetProperty("message", out var messageProp) && messageProp.ValueKind == JsonValueKind.String)
        {
            var raw = messageProp.GetString()!;

            try
            {
                using var inner = JsonDocument.Parse(raw);
                if (inner.RootElement.TryGetProperty("detail", out var detail))
                    return detail.GetString() ?? raw;
            }
            catch (JsonException) { }

            return raw;
        }

        if (@params.TryGetProperty("detail", out var directDetail) && directDetail.ValueKind == JsonValueKind.String)
            return directDetail.GetString()!;

        return "An unknown error occurred";
    }

    private static string? ExtractCodexErrorInfo(JsonElement @params)
    {
        if (@params.TryGetProperty("codexErrorInfo", out var info) && info.ValueKind == JsonValueKind.String)
            return info.GetString();

        return null;
    }
}

internal static partial class CodexStreamParserLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Compact boundary for AgentTask {TaskId}: trigger={Trigger}")]
    public static partial void CompactBoundary(ILogger logger, Guid taskId, CompactTrigger trigger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Turn completed for AgentTask {TaskId}")]
    public static partial void TurnCompleted(ILogger logger, Guid taskId);
}
