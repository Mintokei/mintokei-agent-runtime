using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Acp;

/// <summary>
/// Parses ACP JSON-RPC frames into the shared <see cref="AgentStreamOutput"/> vocabulary via
/// <see cref="Parse"/>: bidirectional frames as pure data (a response → <see cref="ControlResponseReceived"/>,
/// a <c>session/request_permission</c> → <see cref="InteractionRequested"/>), and
/// <c>session/update</c> notifications into two one-way output streams:
///   - <see cref="DeltaOutput"/> — real-time <see cref="DeltaPayload"/> events for token-level
///     streaming to the UI. Text and reasoning chunks flow through this path.
///   - <see cref="MessageOutput"/> — concrete <see cref="AgentMessage"/> entities for tool
///     calls, plans, etc. These are published through the regular persistence channel.
///
/// Text and reasoning chunks are NOT emitted as messages here — the execution service calls
/// <c>ITaskMessageStream.FlushDeltaSnapshot</c> at turn end, which walks the accumulated
/// deltas per block and synthesizes the final persisted AgentMessage / Reasoning rows. This
/// gives us both live streaming AND a single canonical final message without duplication.
///
/// ACP session update kinds handled:
///   - <c>agent_message_chunk</c> / <c>agent_thought_chunk</c> — produce ContentDeltaPayload
///   - <c>tool_call</c> — remember state, close any open text block
///   - <c>tool_call_update</c> — on terminal status, emit the tool message + close text block
///   - <c>plan</c> — emit plan message + close text block
///   - <c>user_message_chunk</c> — ignored (replay on resume)
///   - <c>current_mode_update</c> / <c>available_commands_update</c> / <c>usage_update</c> — logged only
/// </summary>
internal sealed class AcpSessionUpdateParser : IAgentStreamParser
{
    private readonly ILogger _logger;
    private readonly string? _fallbackCwd;

    /// <summary>Dedup guard for consecutive identical chunk text — Copilot re-announces the
    /// same "Running shell command" string as both intent and confirmation.</summary>
    private string? _lastMessageChunk;
    private string? _lastThoughtChunk;

    /// <summary>Delta block tracking. A block represents one contiguous run of text or
    /// reasoning within a turn. Tool calls, plans, or switching between text↔reasoning
    /// all close the current block and start a new one on the next chunk.</summary>
    private int _nextBlockIndex;
    private int _currentBlockIndex;
    private string? _currentBlockType; // "text" | "reasoning" | null (no open block)

    /// <summary>Accumulating text for the current open block — mirrors the deltas we publish
    /// so we can emit a concrete AgentMessage / Reasoning when the block closes. This keeps
    /// the persistence ordering right (pre-tool text → tool → post-tool text) and gives each
    /// assistant message its own CreatedAt timestamp rather than all sharing the turn-end one.</summary>
    private readonly StringBuilder _currentBlockBuffer = new();

    /// <summary>Tool calls seen so far this turn, keyed by toolCallId. Emitted when
    /// <c>tool_call_update</c> reports a terminal status.</summary>
    private readonly Dictionary<string, ToolCallState> _toolCalls = new();

    /// <summary>Copilot's terminal content trailer, e.g. <c>&lt;exited with exit code 0&gt;</c>.</summary>
    private static readonly Regex ExitCodeRegex = new(
        @"<exited with exit code (-?\d+)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public AcpSessionUpdateParser(ILogger logger, string? fallbackCwd = null)
    {
        _logger = logger;
        _fallbackCwd = fallbackCwd;
    }

    /// <summary>Resets chunk-dedup state, delta block tracking, block buffer, and tool-call map.
    /// Call at the start of every new prompt so stale in-flight state doesn't bleed
    /// into the next turn (e.g. after session/load replay).</summary>
    public void Reset()
    {
        _lastMessageChunk = null;
        _lastThoughtChunk = null;
        _nextBlockIndex = 0;
        _currentBlockIndex = 0;
        _currentBlockType = null;
        _currentBlockBuffer.Clear();
        _toolCalls.Clear();
    }

    /// <summary>
    /// Classifies one ACP JSON-RPC frame into the shared vocabulary: a response (id, no method) →
    /// <see cref="ControlResponseReceived"/>; a <c>session/request_permission</c> request →
    /// <see cref="InteractionRequested"/> (pure data); a <c>session/update</c> notification → the
    /// one-way outputs from <see cref="HandleUpdate"/>. Everything else is logged and dropped.
    /// The bidirectional cases are acted on by the service dispatch (which has the handle); the
    /// parser never touches it.
    /// </summary>
    public IEnumerable<AgentStreamOutput> Parse(Guid agentTaskId, JsonElement msg)
    {
        return ParseCore(agentTaskId, msg);
    }

    /// <summary>Pump entry point (<see cref="IAgentStreamParser"/>). ACP has no interrupt-flag
    /// dependency, so <paramref name="isInterrupted"/> is ignored; <paramref name="agentTaskId"/>
    /// tags the produced messages. Delegates to the public <see cref="Parse"/>.</summary>
    IEnumerable<AgentStreamOutput> IAgentStreamParser.Parse(Guid agentTaskId, JsonElement frame, bool isInterrupted)
        => ParseCore(agentTaskId, frame);

    private IEnumerable<AgentStreamOutput> ParseCore(Guid agentTaskId, JsonElement msg)
    {
        var hasId = msg.TryGetProperty("id", out var idProp);
        var method = msg.TryGetProperty("method", out var m) ? m.GetString() : null;

        // Response (id, no method) → route to its pending waiter.
        if (hasId && method is null)
        {
            var id = idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt32().ToString()
                : idProp.GetString();
            return id is { Length: > 0 } ? [new ControlResponseReceived(id, msg)] : [];
        }

        // Agent-originated request (id + method).
        if (hasId)
        {
            if (method == "session/request_permission")
                return BuildPermissionRequest(agentTaskId, idProp, msg);

            AcpSessionUpdateParserLog.UnhandledAgentRequest(_logger, method);
            return [];
        }

        // Notification (no id): session/update is the workhorse.
        if (method == "session/update" && msg.TryGetProperty("params", out var paramsProp))
            return HandleUpdate(agentTaskId, paramsProp);

        if (method != "session/update")
            AcpSessionUpdateParserLog.UnhandledNotification(_logger, method);

        return [];
    }

    /// <summary>Builds an <see cref="InteractionRequested"/> from a <c>session/request_permission</c>
    /// request — pure data: the offered options + the id kind are stashed in <c>ReplyContext</c> for
    /// the dispatch to serialize the reply. The parser never writes to the handle.</summary>
    private IEnumerable<AgentStreamOutput> BuildPermissionRequest(Guid agentTaskId, JsonElement rpcIdProp, JsonElement msg)
    {
        var rpcIdRaw = rpcIdProp.GetRawText();
        var @params = msg.TryGetProperty("params", out var p) ? p : default;
        var toolCall = @params.TryGetProperty("toolCall", out var tc) ? tc : default;
        var options = @params.TryGetProperty("options", out var opt) ? opt : default;

        var title = toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("title", out var tTitle)
            ? tTitle.GetString() : null;
        var kindStr = toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("kind", out var tKind)
            ? tKind.GetString() : null;
        var rawInputJson = toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("rawInput", out var tri)
            ? tri.GetRawText() : null;

        // Pick a default "human-readable content" for the UI: for shell tools, the command; else the title.
        string? content = title;
        string? command = null;
        if (kindStr == "execute" && rawInputJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawInputJson);
                if (doc.RootElement.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    command = c.GetString();
                    content = command ?? title;
                }
            }
            catch (JsonException) { }
        }

        // rpcId can be a number (0, 1, …) or a string — the reply must echo the same kind.
        var useIntId = rpcIdProp.ValueKind == JsonValueKind.Number;

        var message = new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            Role = MessageRole.System,
            Type = MessageType.PermissionRequest,
            Content = content,
            Status = MessageStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
            UserInteraction = new UserInteractionData
            {
                Id = Guid.NewGuid(),
                RequestId = rpcIdRaw,
                ToolName = title ?? kindStr ?? "tool",
                ToolInput = rawInputJson,
                Command = command,
                ReplyContext = AcpInteractionReplyBuilder.BuildContext(options, useIntId),
            },
        };

        return [new InteractionRequested(
            rpcIdRaw, message,
            CacheKey: null,
            NotifyContent: message.Content, NotifyToolName: title ?? kindStr, NotifyCommand: command)];
    }

    /// <summary>
    /// Dispatches a single session/update notification. Yields a mix of deltas and
    /// concrete messages; caller routes each to the appropriate stream.
    /// </summary>
    public IEnumerable<AgentStreamOutput> HandleUpdate(Guid agentTaskId, JsonElement @params)
    {
        if (!@params.TryGetProperty("update", out var update))
            yield break;

        var kind = update.TryGetProperty("sessionUpdate", out var k) ? k.GetString() : null;

        switch (kind)
        {
            case "agent_message_chunk":
            {
                var chunkText = ExtractChunkText(update);
                if (string.IsNullOrEmpty(chunkText) || chunkText == _lastMessageChunk)
                    break;
                _lastMessageChunk = chunkText;
                foreach (var d in EmitChunk(chunkText, "text", agentTaskId))
                    yield return d;
                break;
            }

            case "agent_thought_chunk":
            {
                var chunkText = ExtractChunkText(update);
                if (string.IsNullOrEmpty(chunkText) || chunkText == _lastThoughtChunk)
                    break;
                _lastThoughtChunk = chunkText;
                foreach (var d in EmitChunk(chunkText, "reasoning", agentTaskId))
                    yield return d;
                break;
            }

            case "tool_call":
            {
                // Diff blocks for file edits are captured off both tool_call and
                // tool_call_update (see ParseToolCall / ExtractDiffBlocks) and turned into
                // a FileChange message in BuildToolCallMessage.
                var state = ParseToolCall(update);
                if (state is null)
                    break;

                // Close any open text/reasoning block before the tool renders — the
                // tool message interleaves visually, and the next text chunk should
                // start its own block in the delta log.
                foreach (var o in CloseOpenBlock(agentTaskId))
                    yield return o;

                _toolCalls[state.ToolCallId] = state;
                break;
            }

            case "tool_call_update":
            {
                var toolCallId = update.TryGetProperty("toolCallId", out var tid) ? tid.GetString() : null;
                if (toolCallId is null || !_toolCalls.TryGetValue(toolCallId, out var state))
                {
                    AcpSessionUpdateParserLog.ToolCallUpdateUnknownId(_logger, toolCallId);
                    break;
                }

                UpdateToolCallState(state, update);

                if (IsTerminal(state.Status))
                {
                    _toolCalls.Remove(toolCallId);
                    var msg = BuildToolCallMessage(agentTaskId, state);
                    if (msg is not null)
                        yield return new MessageOutput(msg);
                }
                break;
            }

            case "plan":
            {
                foreach (var o in CloseOpenBlock(agentTaskId))
                    yield return o;

                var planMsg = BuildPlanMessage(agentTaskId, update);
                if (planMsg is not null)
                    yield return new MessageOutput(planMsg);
                break;
            }

            case "user_message_chunk":
                // Replay on resume — already persisted by us.
                break;

            case "usage_update":
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var raw = update.GetRawText();
                    AcpSessionUpdateParserLog.UsageUpdate(_logger, raw);
                }
                var usage = ExtractContextUsage(update);
                if (usage is not null)
                    yield return new DeltaOutput(usage);
                break;
            }

            case "current_mode_update":
            case "available_commands_update":
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var raw = update.GetRawText();
                    AcpSessionUpdateParserLog.ModeOrCommandsUpdate(_logger, kind, raw);
                }
                break;

            default:
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var raw = update.GetRawText();
                    AcpSessionUpdateParserLog.UnhandledSessionUpdate(_logger, kind, raw);
                }
                break;
        }
    }

    /// <summary>Close the currently-open block at turn end, emitting both the trailing
    /// BlockStop delta and the accumulated AgentMessage/Reasoning for any pending text.</summary>
    public IEnumerable<AgentStreamOutput> FlushPendingBlocks(Guid agentTaskId)
    {
        foreach (var o in CloseOpenBlock(agentTaskId))
            yield return o;
    }

    // ── internal helpers ──

    /// <summary>
    /// Emits (optionally) the previous block's BlockStop + final MessageOutput + a
    /// BlockStart for the new block, then the ContentDelta for the incoming chunk text.
    /// Dedup is the caller's responsibility — iterators can't take ref params so the
    /// chunk-already-seen check lives in the <c>HandleUpdate</c> switch arms.
    /// </summary>
    private IEnumerable<AgentStreamOutput> EmitChunk(string chunkText, string blockType, Guid agentTaskId)
    {
        if (_currentBlockType != blockType)
        {
            // Close the previous block (emits both delta stop AND finalized message)
            // before opening a new one of the correct type.
            foreach (var o in CloseOpenBlock(agentTaskId))
                yield return o;

            _currentBlockIndex = _nextBlockIndex++;
            _currentBlockType = blockType;
            yield return new DeltaOutput(new BlockStartPayload(_currentBlockIndex, blockType));
        }

        _currentBlockBuffer.Append(chunkText);
        yield return new DeltaOutput(new ContentDeltaPayload(blockType, _currentBlockIndex, chunkText));
    }

    /// <summary>
    /// Close the current open block: emit BlockStop delta, flush the accumulated buffer
    /// as a MessageOutput (AgentMessage for "text", Reasoning for "reasoning"), and clear
    /// local state so the next chunk starts a fresh block. Returns empty if no block open.
    /// </summary>
    private IEnumerable<AgentStreamOutput> CloseOpenBlock(Guid agentTaskId)
    {
        if (_currentBlockType is null)
            yield break;

        yield return new DeltaOutput(new BlockStopPayload(_currentBlockIndex));

        if (_currentBlockBuffer.Length > 0)
        {
            var text = _currentBlockBuffer.ToString();
            var type = _currentBlockType == "reasoning"
                ? MessageType.Reasoning
                : MessageType.AgentMessage;

            yield return new MessageOutput(new AgentMessage
            {
                Id = Guid.NewGuid(),
                AgentTaskId = agentTaskId,
                Role = MessageRole.Assistant,
                Type = type,
                Content = text,
                Status = MessageStatus.Completed,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            _currentBlockBuffer.Clear();
        }

        _currentBlockType = null;
    }

    private sealed class ToolCallState
    {
        public required string ToolCallId { get; init; }
        public string? Title { get; set; }
        public string? Kind { get; set; }
        public string? Status { get; set; }
        public string? RawInputJson { get; set; }
        public string? ContentText { get; set; }
        public string? RawOutputJson { get; set; }

        /// <summary>ACP <c>diff</c> content blocks seen for this tool call. The block can
        /// arrive on the initial <c>tool_call</c> (Copilot) or a later <c>tool_call_update</c>
        /// (OpenCode), so we accumulate from both and dedup by path when the message is built.</summary>
        public List<AcpDiffBlock> Diffs { get; } = new();
    }

    /// <summary>An ACP <c>{ type: "diff", path, oldText, newText }</c> content block.
    /// <paramref name="OldText"/> is null for a newly-created file.</summary>
    private sealed record AcpDiffBlock(string Path, string? OldText, string NewText);

    private static string? ExtractChunkText(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content))
            return null;

        // content can be { type: "text", text: "..." } OR an array of such blocks.
        if (content.ValueKind == JsonValueKind.Object)
        {
            return content.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String
                    && text.GetString() is { } s)
                {
                    sb.Append(s);
                }
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        return null;
    }

    private static ToolCallState? ParseToolCall(JsonElement update)
    {
        var toolCallId = update.TryGetProperty("toolCallId", out var tid) ? tid.GetString() : null;
        if (toolCallId is null)
            return null;

        var state = new ToolCallState
        {
            ToolCallId = toolCallId,
            Title = update.TryGetProperty("title", out var t) ? t.GetString() : null,
            Kind = update.TryGetProperty("kind", out var k) ? k.GetString() : null,
            Status = update.TryGetProperty("status", out var s) ? s.GetString() : null,
            RawInputJson = update.TryGetProperty("rawInput", out var ri) ? ri.GetRawText() : null,
        };

        // Copilot ships the diff block on the initial tool_call; capture it here too.
        ExtractDiffBlocks(update, state.Diffs);
        return state;
    }

    private static void UpdateToolCallState(ToolCallState state, JsonElement update)
    {
        if (update.TryGetProperty("status", out var s))
            state.Status = s.GetString();

        // Keep the FIRST non-empty title. Copilot's title is stable, but OpenCode's title
        // starts as the tool name ("read"/"grep") and degrades to the argument on completion
        // (the file path for a read, the pattern for a search) — so the pending title is the
        // better display name. The overwritten arg would only duplicate what's already in the
        // args view.
        if (string.IsNullOrEmpty(state.Title)
            && update.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
            state.Title = t.GetString();

        if (update.TryGetProperty("rawOutput", out var ro))
            state.RawOutputJson = ro.GetRawText();

        if (update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder(state.ContentText ?? string.Empty);
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                // Shape: { type: "content", content: { type: "text", text: "..." } }
                if (item.TryGetProperty("content", out var inner)
                    && inner.ValueKind == JsonValueKind.Object
                    && inner.TryGetProperty("text", out var innerText)
                    && innerText.ValueKind == JsonValueKind.String)
                {
                    sb.Append(innerText.GetString());
                }
                // Shape: { type: "text", text: "..." }
                else if (item.TryGetProperty("text", out var direct) && direct.ValueKind == JsonValueKind.String)
                {
                    sb.Append(direct.GetString());
                }
            }
            state.ContentText = sb.ToString();
        }

        // OpenCode ships the diff block on the completed tool_call_update; capture it here too.
        ExtractDiffBlocks(update, state.Diffs);
    }

    private static bool IsTerminal(string? status)
        => status is "completed" or "failed";

    private AgentMessage? BuildToolCallMessage(Guid agentTaskId, ToolCallState state)
    {
        var status = state.Status switch
        {
            "completed" => MessageStatus.Completed,
            "failed" => MessageStatus.Failed,
            _ => MessageStatus.Completed,
        };

        // File edits get a dedicated FileChange row so the chat renders the unified diff
        // instead of an opaque tool blob. The ACP `diff` content block (path/oldText/newText)
        // is the portable source both Copilot and OpenCode emit.
        if (IsFileEditKind(state.Kind) && state.Diffs.Count > 0)
        {
            var fileMsg = new AgentMessage
            {
                Id = Guid.NewGuid(),
                AgentTaskId = agentTaskId,
                ExternalId = state.ToolCallId,
                Role = MessageRole.Tool,
                Type = MessageType.FileChange,
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            foreach (var diff in DedupByPath(state.Diffs))
            {
                fileMsg.FileChanges.Add(new FileChangeData
                {
                    Id = Guid.NewGuid(),
                    Path = diff.Path,
                    Diff = BuildUnifiedDiff(diff.OldText, diff.NewText),
                    ChangeKind = ResolveChangeKind(state.Kind, diff.OldText),
                });
            }

            return fileMsg;
        }

        // Shell commands get a dedicated CommandExecution row for UI niceness.
        if (state.Kind == "execute" && TryExtractCommand(state.RawInputJson, out var command, out var cwd))
        {
            var (exitCode, trimmedOutput) = ExtractExitCode(state.ContentText);

            return new AgentMessage
            {
                Id = Guid.NewGuid(),
                AgentTaskId = agentTaskId,
                ExternalId = state.ToolCallId,
                Role = MessageRole.Tool,
                Type = MessageType.CommandExecution,
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
                CommandExecution = new CommandExecutionData
                {
                    Id = Guid.NewGuid(),
                    Command = command,
                    Cwd = cwd ?? _fallbackCwd ?? string.Empty,
                    ExitCode = exitCode,
                    Output = trimmedOutput,
                },
            };
        }

        // Everything else: generic ToolCall. We stash the rawInput as arguments
        // and the streamed content as a human-readable result string.
        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = state.ToolCallId,
            Role = MessageRole.Tool,
            Type = MessageType.ToolCall,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            ToolCall = new ToolCallData
            {
                Id = Guid.NewGuid(),
                ToolName = state.Title ?? state.Kind ?? "tool",
                Arguments = state.RawInputJson,
                Result = state.ContentText ?? state.RawOutputJson,
            },
        };
    }

    /// <summary>
    /// Some ACP CLIs append a trailer like <c>"&lt;exited with exit code N&gt;"</c> to shell tool
    /// output instead of exposing the exit code as a separate field. Pull it out so the UI
    /// can render the status chip without grepping the text itself.
    /// </summary>
    private static (int? ExitCode, string? TrimmedOutput) ExtractExitCode(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return (null, output);

        var match = ExitCodeRegex.Match(output);
        if (!match.Success)
            return (null, output);

        if (!int.TryParse(match.Groups[1].ValueSpan, out var code))
            return (null, output);

        // Strip the trailer (plus any surrounding whitespace/newlines) so it doesn't
        // also appear in the scrollable output block in the UI.
        var trimmed = (output[..match.Index] + output[(match.Index + match.Length)..]).TrimEnd('\r', '\n', ' ');
        return (code, trimmed.Length == 0 ? null : trimmed);
    }

    private static bool TryExtractCommand(string? rawInputJson, out string command, out string? cwd)
    {
        command = string.Empty;
        cwd = null;

        if (string.IsNullOrEmpty(rawInputJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(rawInputJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String)
                command = c.GetString() ?? string.Empty;

            // Copilot uses `cwd`; OpenCode puts the working dir under `workdir`.
            if (root.TryGetProperty("cwd", out var cw) && cw.ValueKind == JsonValueKind.String)
                cwd = cw.GetString();
            else if (root.TryGetProperty("workdir", out var wd) && wd.ValueKind == JsonValueKind.String)
                cwd = wd.GetString();

            return !string.IsNullOrEmpty(command);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsFileEditKind(string? kind)
        => kind is "edit" or "delete" or "move";

    /// <summary>Collects ACP <c>{ type: "diff", path, oldText, newText }</c> content blocks
    /// from a tool_call / tool_call_update frame's <c>content</c> array.</summary>
    private static void ExtractDiffBlocks(JsonElement update, List<AcpDiffBlock> into)
    {
        if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (!(item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "diff"))
                continue;

            var path = item.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (string.IsNullOrEmpty(path))
                continue;

            var oldText = item.TryGetProperty("oldText", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            var newText = item.TryGetProperty("newText", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            into.Add(new AcpDiffBlock(path, oldText, newText ?? string.Empty));
        }
    }

    /// <summary>Last diff per path wins (updates refine the pending block), preserving first-seen order.</summary>
    private static IEnumerable<AcpDiffBlock> DedupByPath(List<AcpDiffBlock> diffs)
    {
        var latest = new Dictionary<string, AcpDiffBlock>();
        var order = new List<string>();
        foreach (var d in diffs)
        {
            if (!latest.ContainsKey(d.Path))
                order.Add(d.Path);
            latest[d.Path] = d;
        }
        return order.Select(path => latest[path]);
    }

    private static FileChangeKind ResolveChangeKind(string? kind, string? oldText)
        => kind == "delete" ? FileChangeKind.Delete
         : string.IsNullOrEmpty(oldText) ? FileChangeKind.Add
         : FileChangeKind.Update;

    /// <summary>
    /// Synthesizes a unified-diff hunk from the ACP diff block's old/new text. ACP agents send
    /// the replaced fragment (not the whole file), so we emit a headerless hunk with nominal
    /// line numbers — enough for the UI's diff renderer to show removed/added lines. New files
    /// (<paramref name="oldText"/> null/empty) render as a pure add; empty new text as a delete.
    /// </summary>
    private static string BuildUnifiedDiff(string? oldText, string newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var oldStart = oldLines.Length == 0 ? 0 : 1;
        var newStart = newLines.Length == 0 ? 0 : 1;

        var sb = new StringBuilder();
        sb.Append("@@ -").Append(oldStart).Append(',').Append(oldLines.Length)
          .Append(" +").Append(newStart).Append(',').Append(newLines.Length).Append(" @@");
        foreach (var line in oldLines)
            sb.Append('\n').Append('-').Append(line);
        foreach (var line in newLines)
            sb.Append('\n').Append('+').Append(line);
        return sb.ToString();
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var lines = text.Replace("\r\n", "\n").Split('\n');
        // A trailing newline yields a phantom empty final element — drop it so it doesn't
        // render as a blank removed/added line.
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];
        return lines;
    }

    /// <summary>
    /// ACP <c>usage_update</c> shape isn't pinned by the spec — different agents
    /// (Copilot, opencode, …) put the numbers under different keys. We probe the
    /// common ones and fall back to <c>null</c> when nothing recognisable is found,
    /// so a new agent shape just gets logged and ignored rather than blowing up
    /// the parser.
    ///
    /// Recognised shapes (any combination):
    ///   - <c>{ tokens: { input, cachedInput, output, total } }</c>
    ///   - <c>{ usage: { promptTokens, completionTokens, totalTokens } }</c>
    ///   - <c>{ inputTokens, outputTokens, totalTokens }</c> at the top level
    ///   - context window via <c>modelContextWindow</c>, <c>contextWindow</c>, or <c>maxContextTokens</c>
    ///   - model via <c>model</c> or <c>modelId</c>
    ///
    /// Token semantics: <c>cachedInput</c>/<c>cachedInputTokens</c> are treated as a
    /// <em>subset</em> of <c>input</c>/<c>inputTokens</c> (matching the OpenAI/Codex
    /// convention used by Copilot, opencode, and similar OpenAI-backed agents).
    /// Adding the two would double-count the cached portion.
    /// </summary>
    private static ContextUsagePayload? ExtractContextUsage(JsonElement update)
    {
        long used = 0;

        if (update.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
        {
            used = ReadLong(tokens, "input") ?? 0;
            if (used == 0)
                used = ReadLong(tokens, "total") ?? 0;
        }

        if (used == 0 && update.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            used = ReadLong(usage, "promptTokens")
                ?? ReadLong(usage, "totalTokens")
                ?? 0;
        }

        if (used == 0)
        {
            // Top-level flat fields.
            used = ReadLong(update, "inputTokens") ?? 0;
            if (used == 0)
                used = ReadLong(update, "totalTokens") ?? 0;
        }

        long? window = ReadLong(update, "modelContextWindow")
            ?? ReadLong(update, "contextWindow")
            ?? ReadLong(update, "maxContextTokens");

        var model = (update.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null)
            ?? (update.TryGetProperty("modelId", out var mi) && mi.ValueKind == JsonValueKind.String ? mi.GetString() : null);

        if (used <= 0 && window is null)
            return null;

        return new ContextUsagePayload(used > 0 ? used : null, window, model, CostUsd: null);
    }

    private static long? ReadLong(JsonElement obj, string field)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        return obj.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : null;
    }

    private static AgentMessage? BuildPlanMessage(Guid agentTaskId, JsonElement update)
    {
        // ACP plan updates include entries: [{content, priority, status}]
        if (!update.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return null;

        var sb = new StringBuilder();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var status = entry.TryGetProperty("status", out var s) ? s.GetString() : null;
            var content = entry.TryGetProperty("content", out var c) ? c.GetString() : null;
            var marker = status switch
            {
                "completed" => "[x]",
                "in_progress" => "[~]",
                _ => "[ ]",
            };
            sb.Append(marker).Append(' ').AppendLine(content ?? string.Empty);
        }

        if (sb.Length == 0)
            return null;

        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            Role = MessageRole.Assistant,
            Type = MessageType.Plan,
            Content = sb.ToString().TrimEnd(),
            Status = MessageStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}

internal static partial class AcpSessionUpdateParserLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Unhandled ACP agent request {Method}")]
    public static partial void UnhandledAgentRequest(ILogger logger, string? method);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Unhandled ACP notification {Method}")]
    public static partial void UnhandledNotification(ILogger logger, string? method);

    [LoggerMessage(Level = LogLevel.Debug, Message = "tool_call_update for unknown toolCallId {Id}, skipping")]
    public static partial void ToolCallUpdateUnknownId(ILogger logger, string? id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ACP usage_update: {Raw}")]
    public static partial void UsageUpdate(ILogger logger, string raw);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ACP {Kind}: {Raw}")]
    public static partial void ModeOrCommandsUpdate(ILogger logger, string? kind, string raw);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Unhandled ACP sessionUpdate: {Kind} {Raw}")]
    public static partial void UnhandledSessionUpdate(ILogger logger, string? kind, string raw);
}
