using System.Text;
using System.Text.Json;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Claude;

/// <summary>
/// Parses Claude Code CLI stream-json output events into <see cref="AgentMessage"/> entities.
/// Each stdout JSON line is parsed independently. A <see cref="ToolUseInfo"/> registry is used
/// to correlate tool_use blocks with their subsequent tool_result blocks.
/// </summary>
internal static class ClaudeCodeOutputParser
{
    internal readonly record struct ToolUseInfo(string ToolName, string? InputJson);

    public static IReadOnlyList<AgentMessage> ParseAssistantEvent(
        Guid agentTaskId,
        JsonElement root,
        IDictionary<string, ToolUseInfo> toolUseRegistry,
        ILogger? logger = null,
        string? parentToolUseId = null)
    {
        if (!root.TryGetProperty("message", out var message))
            return [];

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return [];

        var messages = new List<AgentMessage>();
        var textBuilder = new StringBuilder();

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var typeProp))
                continue;

            var blockType = typeProp.GetString();

            switch (blockType)
            {
                case "text":
                    if (block.TryGetProperty("text", out var textProp))
                        textBuilder.Append(textProp.GetString());
                    break;

                case "thinking":
                    if (block.TryGetProperty("thinking", out var thinkingProp))
                    {
                        messages.Add(new AgentMessage
                        {
                            Id = Guid.NewGuid(),
                            AgentTaskId = agentTaskId,
                            ParentToolUseId = parentToolUseId,
                            Role = MessageRole.Assistant,
                            Type = MessageType.Reasoning,
                            Content = thinkingProp.GetString(),
                            CreatedAt = DateTimeOffset.UtcNow,
                        });
                    }

                    break;

                case "tool_use":
                    var toolMsg = ParseToolUse(agentTaskId, block, toolUseRegistry, parentToolUseId);
                    if (toolMsg is not null)
                        messages.Add(toolMsg);
                    break;
            }
        }

        if (textBuilder.Length > 0)
        {
            messages.Insert(0, new AgentMessage
            {
                Id = Guid.NewGuid(),
                AgentTaskId = agentTaskId,
                ParentToolUseId = parentToolUseId,
                Role = MessageRole.Assistant,
                Type = MessageType.AgentMessage,
                Content = textBuilder.ToString(),
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        return messages;
    }

    public static IReadOnlyList<AgentMessage> ParseUserEvent(
        Guid agentTaskId,
        JsonElement root,
        IDictionary<string, ToolUseInfo> toolUseRegistry,
        ILogger? logger = null,
        string? parentToolUseId = null)
    {
        if (!root.TryGetProperty("message", out var message))
            return [];

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return [];

        // `tool_use_result` is a sibling of `message` on the user event. For file tools
        // (Edit / Write / MultiEdit) it carries `structuredPatch` + `originalFile` which
        // we use to build a real unified diff. One tool_result per user event in practice.
        string? toolUseResultJson = null;
        if (root.TryGetProperty("tool_use_result", out var turEl)
            && turEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            toolUseResultJson = turEl.GetRawText();
        }

        var messages = new List<AgentMessage>();

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "tool_result")
                continue;

            var toolUseId = block.TryGetProperty("tool_use_id", out var idProp)
                ? idProp.GetString()
                : null;

            if (string.IsNullOrEmpty(toolUseId))
                continue;

            var resultContent = ExtractToolResultContent(block);
            var isError = block.TryGetProperty("is_error", out var errProp)
                          && errProp.ValueKind == JsonValueKind.True;

            toolUseRegistry.TryGetValue(toolUseId, out var toolInfo);

            var msg = CreateToolResultMessage(agentTaskId, toolUseId, toolInfo, resultContent, isError, toolUseResultJson, parentToolUseId);
            if (msg is not null)
                messages.Add(msg);
        }

        return messages;
    }

    // ===== tool_use block → InProgress message =====

    private static AgentMessage? ParseToolUse(
        Guid agentTaskId,
        JsonElement block,
        IDictionary<string, ToolUseInfo> toolUseRegistry,
        string? parentToolUseId = null)
    {
        var toolName = block.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var toolId = block.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

        if (string.IsNullOrEmpty(toolName) || string.IsNullOrEmpty(toolId))
            return null;

        // AskUserQuestion and ExitPlanMode/EnterPlanMode are handled via control_request,
        // so skip them here to avoid duplicate messages in the chat.
        if (toolName is "AskUserQuestion" or "ExitPlanMode" or "EnterPlanMode")
            return null;

        string? inputJson = null;
        if (block.TryGetProperty("input", out var inputEl)
            && inputEl.ValueKind != JsonValueKind.Null
            && inputEl.ValueKind != JsonValueKind.Undefined)
        {
            inputJson = inputEl.GetRawText();
        }

        toolUseRegistry[toolId] = new ToolUseInfo(toolName, inputJson);

        var msg = toolName switch
        {
            // Bash and PowerShell (Windows runners) both carry a `command` field and
            // map to the unified CommandExecution shape.
            "Bash" or "PowerShell" => CreateShellToolUseMessage(agentTaskId, toolId, inputJson),
            "Write" or "Edit" or "MultiEdit" => CreateFileToolUseMessage(agentTaskId, toolId, toolName, inputJson),
            "Agent" => CreateAgentToolUseMessage(agentTaskId, toolId, inputJson),
            _ => CreateGenericToolUseMessage(agentTaskId, toolId, toolName, inputJson),
        };

        msg.ParentToolUseId = parentToolUseId;
        return msg;
    }

    private static AgentMessage CreateShellToolUseMessage(Guid agentTaskId, string toolId, string? inputJson)
    {
        string? command = null;
        if (inputJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(inputJson);
                if (doc.RootElement.TryGetProperty("command", out var cmdProp))
                    command = cmdProp.GetString();
            }
            catch (JsonException) { }
        }

        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = toolId,
            Role = MessageRole.Tool,
            Type = MessageType.CommandExecution,
            Status = MessageStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
            CommandExecution = new CommandExecutionData
            {
                Id = Guid.NewGuid(),
                Command = command ?? string.Empty,
                Cwd = string.Empty,
            },
        };
    }

    private static AgentMessage CreateFileToolUseMessage(
        Guid agentTaskId, string toolId, string toolName, string? inputJson)
    {
        var message = new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = toolId,
            Role = MessageRole.Tool,
            Type = MessageType.FileChange,
            Status = MessageStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        if (inputJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(inputJson);
                var root = doc.RootElement;

                var path = root.TryGetProperty("file_path", out var fp) ? fp.GetString()
                    : root.TryGetProperty("filePath", out var fp2) ? fp2.GetString()
                    : null;

                if (path is not null)
                {
                    message.FileChanges.Add(new FileChangeData
                    {
                        Id = Guid.NewGuid(),
                        Path = path,
                        Diff = string.Empty,
                        ChangeKind = toolName == "Write" ? FileChangeKind.Add : FileChangeKind.Update,
                    });
                }
            }
            catch (JsonException) { }
        }

        return message;
    }

    private static AgentMessage CreateAgentToolUseMessage(
        Guid agentTaskId, string toolId, string? inputJson)
    {
        string? description = null;
        if (inputJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(inputJson);
                if (doc.RootElement.TryGetProperty("description", out var descProp))
                    description = descProp.GetString();
            }
            catch (JsonException) { }
        }

        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = toolId,
            Role = MessageRole.Tool,
            Type = MessageType.SubAgentExecution,
            Status = MessageStatus.InProgress,
            Content = description,
            CreatedAt = DateTimeOffset.UtcNow,
            ToolCall = new ToolCallData
            {
                Id = Guid.NewGuid(),
                ToolName = "Agent",
                Arguments = inputJson,
            },
        };
    }

    private static AgentMessage CreateGenericToolUseMessage(
        Guid agentTaskId, string toolId, string toolName, string? inputJson)
    {
        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = toolId,
            Role = MessageRole.Tool,
            Type = MessageType.ToolCall,
            Status = MessageStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
            ToolCall = new ToolCallData
            {
                Id = Guid.NewGuid(),
                ToolName = toolName,
                Arguments = inputJson,
            },
        };
    }

    // ===== tool_result block → Completed message =====

    private static AgentMessage? CreateToolResultMessage(
        Guid agentTaskId,
        string toolUseId,
        ToolUseInfo toolInfo,
        string? resultContent,
        bool isError,
        string? toolUseResultJson,
        string? parentToolUseId = null)
    {
        var status = isError ? MessageStatus.Failed : MessageStatus.Completed;

        if (!string.IsNullOrEmpty(toolInfo.ToolName))
        {
            var msg = toolInfo.ToolName switch
            {
                "Bash" or "PowerShell" => CreateShellToolResultMessage(agentTaskId, toolUseId, toolInfo, resultContent, status),
                "Write" or "Edit" or "MultiEdit" =>
                    CreateFileToolResultMessage(agentTaskId, toolUseId, toolInfo, resultContent, toolUseResultJson, status),
                "Agent" => CreateAgentToolResultMessage(agentTaskId, toolUseId, toolInfo, resultContent, status),
                _ => CreateGenericToolResultMessage(agentTaskId, toolUseId, toolInfo, resultContent, status),
            };
            msg.ParentToolUseId = parentToolUseId;
            return msg;
        }

        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = toolUseId,
            ParentToolUseId = parentToolUseId,
            Role = MessageRole.Tool,
            Type = MessageType.ToolCall,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            ToolCall = new ToolCallData
            {
                Id = Guid.NewGuid(),
                ToolName = "unknown",
                Result = resultContent,
            },
        };
    }

    private static AgentMessage CreateShellToolResultMessage(
        Guid agentTaskId, string toolUseId, ToolUseInfo toolInfo,
        string? resultContent, MessageStatus status)
    {
        string? command = null;
        if (toolInfo.InputJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(toolInfo.InputJson);
                if (doc.RootElement.TryGetProperty("command", out var cmdProp))
                    command = cmdProp.GetString();
            }
            catch (JsonException) { }
        }

        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = toolUseId,
            Role = MessageRole.Tool,
            Type = MessageType.CommandExecution,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            CommandExecution = new CommandExecutionData
            {
                Id = Guid.NewGuid(),
                Command = command ?? string.Empty,
                Cwd = string.Empty,
                Output = resultContent,
            },
        };
    }

    private static AgentMessage CreateFileToolResultMessage(
        Guid agentTaskId, string toolUseId, ToolUseInfo toolInfo,
        string? resultContent, string? toolUseResultJson, MessageStatus status)
    {
        var message = new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = toolUseId,
            Role = MessageRole.Tool,
            Type = MessageType.FileChange,
            Status = status,
            Content = resultContent,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        foreach (var change in ExtractFileChangesFromToolUseResult(toolUseResultJson, toolInfo))
            message.FileChanges.Add(change);

        return message;
    }

    /// <summary>
    /// Extracts one <see cref="FileChangeData"/> per structured-patch hunk out of the
    /// Claude <c>tool_use_result</c> payload for Edit / Write / MultiEdit.
    ///
    /// Claude's <c>tool_use_result</c> gives us a ready-made patch with real line numbers:
    ///   { filePath, oldString, newString, originalFile, structuredPatch: [{ oldStart, oldLines,
    ///     newStart, newLines, lines: [" ctx", "-del", "+add", ...] }, ...] }
    /// For Write (new file) <c>structuredPatch</c> is empty and <c>content</c> holds the new body,
    /// so we synthesize a pure-add hunk. For failed edits (no tool_use_result) we fall back to the
    /// tool-input path + empty diff so the file row still appears.
    /// </summary>
    private static List<FileChangeData> ExtractFileChangesFromToolUseResult(
        string? toolUseResultJson, ToolUseInfo toolInfo)
    {
        var results = new List<FileChangeData>();
        var pathFromInput = TryGetStringFromJson(toolInfo.InputJson, "file_path")
                            ?? TryGetStringFromJson(toolInfo.InputJson, "filePath");
        var isWrite = toolInfo.ToolName == "Write";

        JsonDocument? doc = null;
        if (toolUseResultJson is not null)
        {
            try
            {
                doc = JsonDocument.Parse(toolUseResultJson);
            }
            catch (JsonException)
            {
                doc = null;
            }
        }

        if (doc is null)
        {
            if (pathFromInput is not null)
            {
                results.Add(new FileChangeData
                {
                    Id = Guid.NewGuid(),
                    Path = pathFromInput,
                    Diff = string.Empty,
                    ChangeKind = isWrite ? FileChangeKind.Add : FileChangeKind.Update,
                });
            }
            return results;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var path = (root.TryGetProperty("filePath", out var fpEl) ? fpEl.GetString() : null)
                       ?? pathFromInput;
            if (path is null)
                return results;

            var isCreate = isWrite
                || (root.TryGetProperty("type", out var tEl) && tEl.GetString() == "create");

            var hasPatch = root.TryGetProperty("structuredPatch", out var patchEl)
                           && patchEl.ValueKind == JsonValueKind.Array
                           && patchEl.GetArrayLength() > 0;

            if (hasPatch)
            {
                foreach (var hunk in patchEl.EnumerateArray())
                {
                    var diff = HunkToUnifiedDiff(hunk);
                    if (diff is null)
                        continue;
                    results.Add(new FileChangeData
                    {
                        Id = Guid.NewGuid(),
                        Path = path,
                        Diff = diff,
                        ChangeKind = isCreate ? FileChangeKind.Add : FileChangeKind.Update,
                    });
                }
                return results;
            }

            if (isCreate)
            {
                var content = (root.TryGetProperty("content", out var cEl) ? cEl.GetString() : null)
                              ?? TryGetStringFromJson(toolInfo.InputJson, "content");
                results.Add(new FileChangeData
                {
                    Id = Guid.NewGuid(),
                    Path = path,
                    Diff = BuildPureAddHunk(content ?? string.Empty),
                    ChangeKind = FileChangeKind.Add,
                });
                return results;
            }

            // Edit that produced no hunks (no-op). Emit row with empty diff so UI still shows it.
            results.Add(new FileChangeData
            {
                Id = Guid.NewGuid(),
                Path = path,
                Diff = string.Empty,
                ChangeKind = FileChangeKind.Update,
            });
            return results;
        }
    }

    /// <summary>
    /// Convert one entry of Claude's <c>structuredPatch</c> array into a unified-diff string.
    /// Input shape: { oldStart, oldLines, newStart, newLines, lines: [" ctx", "-del", "+add"] }.
    /// Output: <c>@@ -a,b +c,d @@\n + lines joined by \n</c>. The <c>lines</c> already carry
    /// the leading space/+/- marker, so we just re-join them.
    /// </summary>
    private static string? HunkToUnifiedDiff(JsonElement hunk)
    {
        if (hunk.ValueKind != JsonValueKind.Object)
            return null;
        if (!hunk.TryGetProperty("lines", out var linesEl) || linesEl.ValueKind != JsonValueKind.Array)
            return null;

        var oldStart = hunk.TryGetProperty("oldStart", out var osEl) ? osEl.GetInt32() : 1;
        var oldLines = hunk.TryGetProperty("oldLines", out var olEl) ? olEl.GetInt32() : 0;
        var newStart = hunk.TryGetProperty("newStart", out var nsEl) ? nsEl.GetInt32() : 1;
        var newLines = hunk.TryGetProperty("newLines", out var nlEl) ? nlEl.GetInt32() : 0;

        var sb = new StringBuilder();
        sb.Append("@@ -").Append(oldStart).Append(',').Append(oldLines)
          .Append(" +").Append(newStart).Append(',').Append(newLines).Append(" @@");
        foreach (var lineEl in linesEl.EnumerateArray())
        {
            sb.Append('\n').Append(lineEl.GetString() ?? string.Empty);
        }
        return sb.ToString();
    }

    /// <summary>Synthetic add-only hunk for Write on a brand-new file.</summary>
    private static string BuildPureAddHunk(string content)
    {
        var lines = content.Split('\n');
        var count = lines.Length;
        // A trailing newline produces an empty final element — don't emit it as a `+` line.
        var effective = (count > 0 && lines[^1].Length == 0) ? count - 1 : count;

        var sb = new StringBuilder();
        sb.Append("@@ -0,0 +1,").Append(effective).Append(" @@");
        for (var i = 0; i < effective; i++)
            sb.Append('\n').Append('+').Append(lines[i]);
        return sb.ToString();
    }

    private static string? TryGetStringFromJson(string? json, string property)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var el) ? el.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AgentMessage CreateAgentToolResultMessage(
        Guid agentTaskId, string toolUseId, ToolUseInfo toolInfo,
        string? resultContent, MessageStatus status)
    {
        // Preserve the description from the original tool input as Content (used as the title),
        // while the actual result goes only in ToolCall.Result.
        string? description = null;
        if (toolInfo.InputJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(toolInfo.InputJson);
                if (doc.RootElement.TryGetProperty("description", out var descProp))
                    description = descProp.GetString();
            }
            catch (JsonException) { }
        }

        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = toolUseId,
            Role = MessageRole.Tool,
            Type = MessageType.SubAgentExecution,
            Status = status,
            Content = description,
            CreatedAt = DateTimeOffset.UtcNow,
            ToolCall = new ToolCallData
            {
                Id = Guid.NewGuid(),
                ToolName = "Agent",
                Arguments = toolInfo.InputJson,
                Result = resultContent,
            },
        };
    }

    private static AgentMessage CreateGenericToolResultMessage(
        Guid agentTaskId, string toolUseId, ToolUseInfo toolInfo,
        string? resultContent, MessageStatus status)
    {
        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = toolUseId,
            Role = MessageRole.Tool,
            Type = MessageType.ToolCall,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            ToolCall = new ToolCallData
            {
                Id = Guid.NewGuid(),
                ToolName = toolInfo.ToolName,
                Arguments = toolInfo.InputJson,
                Result = resultContent,
            },
        };
    }

    // ===== Stream event (delta) parsing =====

    /// <summary>
    /// Parses a <c>stream_event</c> JSON line (emitted when <c>--include-partial-messages</c> is used)
    /// into a <see cref="DeltaPayload"/>. Returns <c>null</c> for unrecognised event subtypes.
    /// <para>
    /// <paramref name="parentToolUseId"/> is the <c>parent_tool_use_id</c> field from the
    /// surrounding stream_event root, threaded onto block_start / content_delta payloads so
    /// the frontend can attribute live blocks to the correct sub-agent container.
    /// </para>
    /// </summary>
    public static DeltaPayload? ParseStreamEvent(JsonElement root, string? parentToolUseId = null)
    {
        if (!root.TryGetProperty("event", out var evt))
            return null;

        if (!evt.TryGetProperty("type", out var evtTypeProp))
            return null;

        var evtType = evtTypeProp.GetString();

        return evtType switch
        {
            "message_start" => new TurnPayload(IsStart: true),
            "content_block_start" => ParseBlockStart(evt, parentToolUseId),
            "content_block_delta" => ParseContentBlockDelta(evt, parentToolUseId),
            "content_block_stop" => ParseBlockStop(evt),
            // message_stop is per-Anthropic-message (fires once per LLM request,
            // multiple times per user turn when tools run). The user-visible
            // turn_stop is emitted at the CLI `result` event instead — see
            // ClaudeStreamParser.HandleResult (TurnEnded → the base dispatcher).
            "message_stop" => null,
            _ => null,
        };
    }

    private static BlockStartPayload ParseBlockStart(JsonElement evt, string? parentToolUseId)
    {
        var index = evt.TryGetProperty("index", out var idxProp) ? idxProp.GetInt32() : 0;
        var contentBlock = evt.TryGetProperty("content_block", out var cb) ? cb : default;
        var rawType = contentBlock.ValueKind != JsonValueKind.Undefined
            && contentBlock.TryGetProperty("type", out var btProp)
                ? btProp.GetString()
                : "text";

        string? toolName = null;
        if (rawType == "tool_use"
            && contentBlock.ValueKind != JsonValueKind.Undefined
            && contentBlock.TryGetProperty("name", out var tnProp))
        {
            toolName = tnProp.GetString();
        }

        var mappedType = rawType switch
        {
            "thinking" => "reasoning",
            _ => rawType!,
        };

        return new BlockStartPayload(index, mappedType, toolName, parentToolUseId);
    }

    private static ContentDeltaPayload? ParseContentBlockDelta(JsonElement evt, string? parentToolUseId)
    {
        var index = evt.TryGetProperty("index", out var idxProp) ? idxProp.GetInt32() : 0;

        if (!evt.TryGetProperty("delta", out var delta))
            return null;

        var deltaType = delta.TryGetProperty("type", out var dtProp) ? dtProp.GetString() : null;

        var (mappedType, text) = deltaType switch
        {
            "text_delta" => ("text", delta.TryGetProperty("text", out var t) ? t.GetString() : null),
            "thinking_delta" => ("reasoning", delta.TryGetProperty("thinking", out var t) ? t.GetString() : null),
            "input_json_delta" => ("tool_input", delta.TryGetProperty("partial_json", out var t) ? t.GetString() : null),
            _ => ((string?)null, (string?)null),
        };

        if (mappedType is null || text is null)
            return null;

        return new ContentDeltaPayload(mappedType, index, text, ToolName: null, ParentToolUseId: parentToolUseId);
    }

    private static BlockStopPayload ParseBlockStop(JsonElement evt)
    {
        var index = evt.TryGetProperty("index", out var idxProp) ? idxProp.GetInt32() : 0;
        return new BlockStopPayload(index);
    }

    // ===== Helpers =====

    private static string? ExtractToolResultContent(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var contentProp))
            return null;

        if (contentProp.ValueKind == JsonValueKind.String)
            return contentProp.GetString();

        if (contentProp.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in contentProp.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && item.TryGetProperty("text", out var txt))
                {
                    sb.Append(txt.GetString());
                }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        return null;
    }

    /// <summary>
    /// Parses a Claude Code <c>type:"system", subtype:"compact_boundary"</c> event into an
    /// <see cref="AgentMessage"/> + attached <see cref="CompactBoundaryData"/>.
    /// The event carries <c>compact_metadata</c> with the trigger source, pre/post token
    /// counts, duration, and the list of tools discovered pre-compaction (stored for audit
    /// only, not rendered in UI). Summary text is filled in separately from the synthetic
    /// user-summary message that always follows this event in the stream.
    /// </summary>
    public static AgentMessage ParseCompactBoundaryEvent(Guid agentTaskId, JsonElement root)
    {
        var trigger = CompactTrigger.Auto;
        long? preTokens = null;
        long? postTokens = null;
        long? durationMs = null;
        string? toolsBeforeJson = null;

        if (root.TryGetProperty("compact_metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("trigger", out var t)
                && t.GetString() == "manual")
                trigger = CompactTrigger.Manual;

            if (metadata.TryGetProperty("pre_tokens", out var pre)
                && pre.ValueKind == JsonValueKind.Number)
                preTokens = pre.GetInt64();

            if (metadata.TryGetProperty("post_tokens", out var post)
                && post.ValueKind == JsonValueKind.Number)
                postTokens = post.GetInt64();

            if (metadata.TryGetProperty("duration_ms", out var dur)
                && dur.ValueKind == JsonValueKind.Number)
                durationMs = dur.GetInt64();

            if (metadata.TryGetProperty("pre_compact_discovered_tools", out var tools)
                && tools.ValueKind == JsonValueKind.Array)
                toolsBeforeJson = tools.GetRawText();
        }

        var messageId = Guid.NewGuid();
        return new AgentMessage
        {
            Id = messageId,
            AgentTaskId = agentTaskId,
            Role = MessageRole.System,
            Type = MessageType.CompactBoundary,
            Status = MessageStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
            CompactBoundary = new CompactBoundaryData
            {
                Id = Guid.NewGuid(),
                Trigger = trigger,
                PreTokens = preTokens,
                PostTokens = postTokens,
                DurationMs = durationMs,
                Success = true,
                ToolsBeforeJson = toolsBeforeJson,
            },
        };
    }

    /// <summary>
    /// Extracts the summary text from a synthetic user event emitted by Claude Code
    /// immediately after <c>compact_boundary</c>. Returns null if the event doesn't carry
    /// <c>isSynthetic:true</c> or the content isn't a plain string.
    /// </summary>
    public static string? ExtractCompactSummaryFromSyntheticUserEvent(JsonElement root)
    {
        var isSynthetic = root.TryGetProperty("isSynthetic", out var synProp)
                          && synProp.ValueKind == JsonValueKind.True;
        if (!isSynthetic)
            return null;

        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String)
            return null;

        return content.GetString();
    }
}
