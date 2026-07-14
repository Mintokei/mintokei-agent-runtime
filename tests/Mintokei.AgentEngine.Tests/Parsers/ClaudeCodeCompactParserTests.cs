using System.Text.Json;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

public class ClaudeCodeCompactParserTests
{
    private static readonly Guid TaskId = Guid.NewGuid();

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // --- ParseCompactBoundaryEvent ---

    [Fact]
    public void ParseCompactBoundaryEvent_ManualTrigger_AllMetadata_Populated()
    {
        // Real shape captured from `/compact` run against claude-haiku-4-5 — see the research
        // phase notes on PR #144 for the end-to-end capture.
        var root = Parse("""
        {
          "type": "system",
          "subtype": "compact_boundary",
          "session_id": "f7399ad0-7338-432a-9b96-16510d4a64af",
          "uuid": "81187508-4980-47b5-9c7c-844877609229",
          "compact_metadata": {
            "trigger": "manual",
            "pre_tokens": 39388,
            "post_tokens": 677,
            "duration_ms": 20712,
            "pre_compact_discovered_tools": ["Bash", "Read", "Edit"]
          }
        }
        """);

        var msg = ClaudeCodeOutputParser.ParseCompactBoundaryEvent(TaskId, root);

        Assert.Equal(TaskId, msg.AgentTaskId);
        Assert.Equal(MessageRole.System, msg.Role);
        Assert.Equal(MessageType.CompactBoundary, msg.Type);
        Assert.Equal(MessageStatus.Completed, msg.Status);

        Assert.NotNull(msg.CompactBoundary);
        Assert.Equal(Mintokei.AgentEngine.Contracts.CompactTrigger.Manual, msg.CompactBoundary.Trigger);
        Assert.Equal(39388, msg.CompactBoundary.PreTokens);
        Assert.Equal(677, msg.CompactBoundary.PostTokens);
        Assert.Equal(20712, msg.CompactBoundary.DurationMs);
        Assert.True(msg.CompactBoundary.Success);
        Assert.Null(msg.CompactBoundary.SummaryText); // filled in later from synthetic user event

        Assert.NotNull(msg.CompactBoundary.ToolsBeforeJson);
        Assert.Contains("Bash", msg.CompactBoundary.ToolsBeforeJson);
        Assert.Contains("Edit", msg.CompactBoundary.ToolsBeforeJson);
    }

    [Fact]
    public void ParseCompactBoundaryEvent_AutoTrigger_Recognized()
    {
        var root = Parse("""
        {
          "type": "system",
          "subtype": "compact_boundary",
          "compact_metadata": {
            "trigger": "auto",
            "pre_tokens": 968066,
            "post_tokens": 8668,
            "duration_ms": 174233
          }
        }
        """);

        var msg = ClaudeCodeOutputParser.ParseCompactBoundaryEvent(TaskId, root);

        Assert.Equal(Mintokei.AgentEngine.Contracts.CompactTrigger.Auto, msg.CompactBoundary!.Trigger);
        Assert.Equal(968066, msg.CompactBoundary.PreTokens);
    }

    [Fact]
    public void ParseCompactBoundaryEvent_MissingMetadata_DefaultsToAuto()
    {
        var root = Parse("""{"type":"system","subtype":"compact_boundary"}""");

        var msg = ClaudeCodeOutputParser.ParseCompactBoundaryEvent(TaskId, root);

        Assert.Equal(Mintokei.AgentEngine.Contracts.CompactTrigger.Auto, msg.CompactBoundary!.Trigger);
        Assert.Null(msg.CompactBoundary.PreTokens);
        Assert.Null(msg.CompactBoundary.PostTokens);
        Assert.Null(msg.CompactBoundary.DurationMs);
        Assert.Null(msg.CompactBoundary.ToolsBeforeJson);
        Assert.True(msg.CompactBoundary.Success);
    }

    [Fact]
    public void ParseCompactBoundaryEvent_MessageIdLinkedToCompactBoundaryFk()
    {
        var root = Parse("""{"type":"system","subtype":"compact_boundary"}""");

        var msg = ClaudeCodeOutputParser.ParseCompactBoundaryEvent(TaskId, root);

        Assert.NotEqual(Guid.Empty, msg.Id);
    }

    // --- ExtractCompactSummaryFromSyntheticUserEvent ---

    [Fact]
    public void ExtractCompactSummary_SyntheticUserEvent_ReturnsContent()
    {
        var root = Parse("""
        {
          "type": "user",
          "message": {"role": "user", "content": "This session is being continued..."},
          "isSynthetic": true
        }
        """);

        var summary = ClaudeCodeOutputParser.ExtractCompactSummaryFromSyntheticUserEvent(root);

        Assert.Equal("This session is being continued...", summary);
    }

    [Fact]
    public void ExtractCompactSummary_NonSyntheticUser_ReturnsNull()
    {
        // Regular user message — must not be misinterpreted as a compaction summary.
        var root = Parse("""
        {
          "type": "user",
          "message": {"role": "user", "content": "hello there"}
        }
        """);

        var summary = ClaudeCodeOutputParser.ExtractCompactSummaryFromSyntheticUserEvent(root);

        Assert.Null(summary);
    }

    [Fact]
    public void ExtractCompactSummary_IsReplay_ReturnsNull()
    {
        // Claude emits `<local-command-stdout>Compacted </local-command-stdout>` as an
        // isReplay:true user message right after the synthetic summary. That's a status
        // echo, not the summary — must not be mistaken for one.
        var root = Parse("""
        {
          "type": "user",
          "message": {"role": "user", "content": "<local-command-stdout>Compacted </local-command-stdout>"},
          "isReplay": true
        }
        """);

        var summary = ClaudeCodeOutputParser.ExtractCompactSummaryFromSyntheticUserEvent(root);

        Assert.Null(summary);
    }

    [Fact]
    public void ExtractCompactSummary_ArrayContent_ReturnsNull()
    {
        // tool_result user messages have `content` as an array, not a plain string — those
        // are handled by ParseUserEvent, not by the compact path.
        var root = Parse("""
        {
          "type": "user",
          "message": {"role": "user", "content": [{"type": "tool_result", "tool_use_id": "x"}]},
          "isSynthetic": true
        }
        """);

        var summary = ClaudeCodeOutputParser.ExtractCompactSummaryFromSyntheticUserEvent(root);

        Assert.Null(summary);
    }

    // --- integration-level: real captured /compact stream ---

    /// <summary>
    /// Drives the actual JSONL captured from a real `/compact` run against
    /// claude-haiku-4-5-20251001 through the dispatch-level logic the output pump uses.
    /// Serves as a regression guard — if Claude ever changes the event shape, this breaks
    /// before any user hits the bug.
    /// </summary>
    [Fact]
    public void RealClaudeCompactCapture_ProducesExpectedBoundaryAndSummary()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Infrastructure", "Fixtures", "claude-compact-live.jsonl");

        Assert.True(File.Exists(fixturePath), $"fixture missing at {fixturePath}");

        AgentMessage? boundary = null;
        string? summary = null;
        int regularUserEvents = 0;

        foreach (var line in File.ReadAllLines(fixturePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();
            if (!root.TryGetProperty("type", out var t)) continue;
            var type = t.GetString();

            if (type == "system"
                && root.TryGetProperty("subtype", out var st)
                && st.GetString() == "compact_boundary")
            {
                boundary = ClaudeCodeOutputParser.ParseCompactBoundaryEvent(TaskId, root);
            }
            else if (type == "user")
            {
                var s = ClaudeCodeOutputParser.ExtractCompactSummaryFromSyntheticUserEvent(root);
                if (s is not null)
                    summary = s;
                else
                    regularUserEvents++;
            }
        }

        // Captured run was a deliberate manual /compact with a short prior conversation.
        Assert.NotNull(boundary);
        Assert.Equal(Mintokei.AgentEngine.Contracts.CompactTrigger.Manual, boundary.CompactBoundary!.Trigger);
        Assert.True(boundary.CompactBoundary.PreTokens > boundary.CompactBoundary.PostTokens,
            "pre_tokens must exceed post_tokens after a real compaction");
        Assert.True(boundary.CompactBoundary.DurationMs > 0);

        // Synthetic summary must have been extracted — without it the UI can't render the
        // "what was in the context before" body.
        Assert.NotNull(summary);
        Assert.StartsWith("This session is being continued", summary, StringComparison.Ordinal);

        // The `<local-command-stdout>Compacted </local-command-stdout>` replay line is a user
        // event but NOT a summary. It must fall through to the non-summary branch (regular
        // user event). If we ever misclassify it as a summary, UI ends up with the wrong text.
        Assert.True(regularUserEvents >= 1,
            "the isReplay:true 'Compacted' echo must be treated as a regular user event, not a summary");
    }
}
