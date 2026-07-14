using System.Text;
using System.Text.Json;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Codex;

internal static class CodexThreadItemParser
{
    public static AgentMessage? Parse(Guid agentTaskId, JsonElement item, ILogger? logger = null)
    {
        try
        {
            if (!item.TryGetProperty("type", out var typeProp))
            {
                logger?.LogWarning("ThreadItem missing 'type' property, skipping");
                return null;
            }

            var type = typeProp.GetString();

            return type switch
            {
                "agentMessage" => ParseAgentMessage(agentTaskId, item),
                "plan" => ParsePlan(agentTaskId, item),
                "reasoning" => ParseReasoning(agentTaskId, item),
                "commandExecution" => ParseCommandExecution(agentTaskId, item),
                "fileChange" => ParseFileChange(agentTaskId, item),
                "mcpToolCall" => ParseMcpToolCall(agentTaskId, item),
                "dynamicToolCall" => ParseDynamicToolCall(agentTaskId, item),
                "webSearch" => ParseWebSearch(agentTaskId, item),
                "contextCompaction" => ParseContextCompaction(agentTaskId, item),
                "userMessage" => null, // Already persisted by us
                _ => LogAndSkip(logger, type),
            };
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse ThreadItem, skipping");
            return null;
        }
    }

    private static AgentMessage? LogAndSkip(ILogger? logger, string? type)
    {
        logger?.LogDebug("Skipping unsupported ThreadItem type: {Type}", type);
        return null;
    }

    private static AgentMessage ParseAgentMessage(Guid agentTaskId, JsonElement item)
    {
        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = GetStringOrNull(item, "id"),
            Role = MessageRole.Assistant,
            Type = MessageType.AgentMessage,
            Content = GetStringOrNull(item, "text"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static AgentMessage ParsePlan(Guid agentTaskId, JsonElement item)
    {
        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = GetStringOrNull(item, "id"),
            Role = MessageRole.Assistant,
            Type = MessageType.Plan,
            Content = GetStringOrNull(item, "text"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static AgentMessage ParseReasoning(Guid agentTaskId, JsonElement item)
    {
        string? content = null;
        if (item.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
        {
            content = string.Join("\n", EnumerateStrings(contentArr));
        }

        string? metadata = null;
        if (item.TryGetProperty("summary", out var summaryArr) && summaryArr.ValueKind == JsonValueKind.Array)
        {
            metadata = string.Join("\n", EnumerateStrings(summaryArr));
        }

        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = GetStringOrNull(item, "id"),
            Role = MessageRole.Assistant,
            Type = MessageType.Reasoning,
            Content = content,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static AgentMessage ParseCommandExecution(Guid agentTaskId, JsonElement item)
    {
        var message = new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = GetStringOrNull(item, "id"),
            Role = MessageRole.Tool,
            Type = MessageType.CommandExecution,
            Status = MapStatus(GetStringOrNull(item, "status")),
            DurationMs = GetLongOrNull(item, "durationMs"),
            CreatedAt = DateTimeOffset.UtcNow,
            CommandExecution = new CommandExecutionData
            {
                Id = Guid.NewGuid(),
                Command = GetStringOrNull(item, "command") ?? string.Empty,
                Cwd = GetStringOrNull(item, "cwd") ?? string.Empty,
                ExitCode = GetIntOrNull(item, "exitCode"),
                Output = GetStringOrNull(item, "aggregatedOutput"),
            },
        };

        return message;
    }

    private static AgentMessage ParseFileChange(Guid agentTaskId, JsonElement item)
    {
        var message = new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = GetStringOrNull(item, "id"),
            Role = MessageRole.Tool,
            Type = MessageType.FileChange,
            Status = MapStatus(GetStringOrNull(item, "status")),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        if (item.TryGetProperty("changes", out var changesArr) && changesArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in changesArr.EnumerateArray())
            {
                var kindStr = change.TryGetProperty("kind", out var kindObj)
                              && kindObj.TryGetProperty("type", out var kindType)
                    ? kindType.GetString()
                    : null;
                var kind = MapFileChangeKind(kindStr);
                var rawDiff = GetStringOrNull(change, "diff") ?? string.Empty;

                message.FileChanges.Add(new FileChangeData
                {
                    Id = Guid.NewGuid(),
                    Path = GetStringOrNull(change, "path") ?? string.Empty,
                    Diff = NormalizeCodexDiff(rawDiff, kind),
                    ChangeKind = kind,
                });
            }
        }

        return message;
    }

    /// <summary>
    /// Codex emits updates as proper unified diffs but adds/deletes arrive as the raw file body
    /// with no `@@` header and no `+` / `-` prefix — the chat renderer needs a real hunk to pick up
    /// Shiki highlighting. For Add we wrap the body in a pure-add hunk; for Delete, a pure-delete
    /// hunk. For Update we pass the unified diff through unchanged.
    /// </summary>
    private static string NormalizeCodexDiff(string raw, FileChangeKind kind)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        // Already starts with a unified-diff hunk — trust it (Update case, or a future Codex
        // version that normalizes add/delete server-side).
        if (raw.StartsWith("@@", StringComparison.Ordinal)) return raw;
        if (kind != FileChangeKind.Add && kind != FileChangeKind.Delete) return raw;

        var marker = kind == FileChangeKind.Add ? '+' : '-';
        var lines = raw.Split('\n');
        var count = lines.Length;
        // A trailing newline splits into an extra empty element — drop it so the hunk
        // line count matches the real content.
        if (count > 0 && lines[^1].Length == 0) count--;

        var sb = new StringBuilder();
        sb.Append("@@ ")
          .Append(kind == FileChangeKind.Add ? "-0,0 +1," : "-1,")
          .Append(count)
          .Append(kind == FileChangeKind.Add ? " @@" : " +0,0 @@");
        for (var i = 0; i < count; i++)
            sb.Append('\n').Append(marker).Append(lines[i]);
        return sb.ToString();
    }

    private static AgentMessage ParseMcpToolCall(Guid agentTaskId, JsonElement item)
    {
        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = GetStringOrNull(item, "id"),
            Role = MessageRole.Tool,
            Type = MessageType.ToolCall,
            Status = MapStatus(GetStringOrNull(item, "status")),
            DurationMs = GetLongOrNull(item, "durationMs"),
            CreatedAt = DateTimeOffset.UtcNow,
            ToolCall = new ToolCallData
            {
                Id = Guid.NewGuid(),
                ToolName = GetStringOrNull(item, "tool") ?? string.Empty,
                ServerName = GetStringOrNull(item, "server"),
                Arguments = GetRawJsonOrNull(item, "args"),
                Result = GetRawJsonOrNull(item, "result"),
                Error = GetNestedStringOrNull(item, "error", "message"),
            },
        };
    }

    private static AgentMessage ParseDynamicToolCall(Guid agentTaskId, JsonElement item)
    {
        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = GetStringOrNull(item, "id"),
            Role = MessageRole.Tool,
            Type = MessageType.ToolCall,
            Status = MapStatus(GetStringOrNull(item, "status")),
            DurationMs = GetLongOrNull(item, "durationMs"),
            CreatedAt = DateTimeOffset.UtcNow,
            ToolCall = new ToolCallData
            {
                Id = Guid.NewGuid(),
                ToolName = GetStringOrNull(item, "tool") ?? string.Empty,
                ServerName = null,
                Arguments = GetRawJsonOrNull(item, "args"),
                Result = GetRawJsonOrNull(item, "contentItems"),
                Error = GetNestedStringOrNull(item, "error", "message"),
            },
        };
    }

    private static AgentMessage ParseWebSearch(Guid agentTaskId, JsonElement item)
    {
        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            ExternalId = GetStringOrNull(item, "id"),
            Role = MessageRole.Assistant,
            Type = MessageType.WebSearch,
            Content = GetStringOrNull(item, "query"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Codex's contextCompaction item only carries <c>{id, type}</c> — no tokens, no
    /// summary, no duration. The actual summary lives encrypted in the rollout and is
    /// never exposed to clients. We emit a boundary row so the UI still gets a marker.
    /// Trigger defaults to <see cref="CompactTrigger.Auto"/>; the service overrides to
    /// <see cref="CompactTrigger.Manual"/> when it initiated the compaction.
    /// </summary>
    public static AgentMessage ParseContextCompaction(Guid agentTaskId, JsonElement item)
    {
        var messageId = Guid.NewGuid();
        return new AgentMessage
        {
            Id = messageId,
            AgentTaskId = agentTaskId,
            ExternalId = GetStringOrNull(item, "id"),
            Role = MessageRole.System,
            Type = MessageType.CompactBoundary,
            Status = MessageStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
            CompactBoundary = new CompactBoundaryData
            {
                Id = Guid.NewGuid(),
                Trigger = CompactTrigger.Auto,
                Success = true,
            },
        };
    }

    private static MessageStatus? MapStatus(string? status)
    {
        return status switch
        {
            "inProgress" => MessageStatus.InProgress,
            "completed" => MessageStatus.Completed,
            "failed" => MessageStatus.Failed,
            "declined" => MessageStatus.Declined,
            _ => null,
        };
    }

    private static FileChangeKind MapFileChangeKind(string? kind)
    {
        return kind switch
        {
            "add" => FileChangeKind.Add,
            "delete" => FileChangeKind.Delete,
            _ => FileChangeKind.Update,
        };
    }

    private static string? GetStringOrNull(JsonElement el, string property)
    {
        return el.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;
    }

    private static int? GetIntOrNull(JsonElement el, string property)
    {
        return el.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.Number
            ? val.GetInt32()
            : null;
    }

    private static long? GetLongOrNull(JsonElement el, string property)
    {
        return el.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.Number
            ? val.GetInt64()
            : null;
    }

    private static string? GetRawJsonOrNull(JsonElement el, string property)
    {
        return el.TryGetProperty(property, out var val)
               && val.ValueKind != JsonValueKind.Null
               && val.ValueKind != JsonValueKind.Undefined
            ? val.GetRawText()
            : null;
    }

    private static string? GetNestedStringOrNull(JsonElement el, string outerProp, string innerProp)
    {
        return el.TryGetProperty(outerProp, out var outer)
               && outer.ValueKind == JsonValueKind.Object
               && outer.TryGetProperty(innerProp, out var inner)
               && inner.ValueKind == JsonValueKind.String
            ? inner.GetString()
            : null;
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement array)
    {
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (s is not null)
                    yield return s;
            }
        }
    }
}
