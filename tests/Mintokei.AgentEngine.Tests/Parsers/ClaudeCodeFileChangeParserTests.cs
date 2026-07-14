using System.Text.Json;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Verifies that Edit / Write / MultiEdit tool results from the Claude Code stream-json
/// output are turned into proper unified-diff strings using <c>tool_use_result.structuredPatch</c>.
/// The JSON fixtures here are captured from a live <c>claude --print --output-format=stream-json</c>
/// run to match the real wire format.
/// </summary>
public class ClaudeCodeFileChangeParserTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void EditToolResult_ProducesUnifiedDiffWithRealLineNumbers()
    {
        var registry = new Dictionary<string, ClaudeCodeOutputParser.ToolUseInfo>();

        // tool_use block: the Edit request.
        var assistantEvt = Parse("""
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu_1","name":"Edit",
              "input":{"file_path":"/tmp/hello.txt","old_string":"line 2","new_string":"line TWO","replace_all":false}}]}}
            """);
        ClaudeCodeOutputParser.ParseAssistantEvent(Guid.NewGuid(), assistantEvt, registry);

        // tool_result block: sibling `tool_use_result` carries structuredPatch with real line numbers.
        var userEvt = Parse("""
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu_1",
              "content":"The file /tmp/hello.txt has been updated successfully."}]},
             "tool_use_result":{"filePath":"/tmp/hello.txt","oldString":"line 2","newString":"line TWO",
              "originalFile":"line 1\nline 2\nline 3\n",
              "structuredPatch":[{"oldStart":1,"oldLines":3,"newStart":1,"newLines":3,
                "lines":[" line 1","-line 2","+line TWO"," line 3"]}],
              "userModified":false,"replaceAll":false}}
            """);
        var msgs = ClaudeCodeOutputParser.ParseUserEvent(Guid.NewGuid(), userEvt, registry);

        var fileMsg = Assert.Single(msgs);
        var change = Assert.Single(fileMsg.FileChanges);
        Assert.Equal("/tmp/hello.txt", change.Path);
        Assert.Equal(Mintokei.AgentEngine.Contracts.FileChangeKind.Update, change.ChangeKind);
        Assert.Equal(
            "@@ -1,3 +1,3 @@\n line 1\n-line 2\n+line TWO\n line 3",
            change.Diff);
    }

    [Fact]
    public void WriteToolResult_NewFile_SynthesizesPureAddHunk()
    {
        var registry = new Dictionary<string, ClaudeCodeOutputParser.ToolUseInfo>();
        var assistantEvt = Parse("""
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu_2","name":"Write",
              "input":{"file_path":"/tmp/new.txt","content":"hello\nworld"}}]}}
            """);
        ClaudeCodeOutputParser.ParseAssistantEvent(Guid.NewGuid(), assistantEvt, registry);

        // Write emits structuredPatch: [] + type: "create" + content: the full new body.
        var userEvt = Parse("""
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu_2",
              "content":"File created successfully at: /tmp/new.txt"}]},
             "tool_use_result":{"type":"create","filePath":"/tmp/new.txt",
              "content":"hello\nworld","structuredPatch":[],"originalFile":null,"userModified":false}}
            """);
        var msgs = ClaudeCodeOutputParser.ParseUserEvent(Guid.NewGuid(), userEvt, registry);

        var change = Assert.Single(Assert.Single(msgs).FileChanges);
        Assert.Equal(Mintokei.AgentEngine.Contracts.FileChangeKind.Add, change.ChangeKind);
        Assert.Equal("@@ -0,0 +1,2 @@\n+hello\n+world", change.Diff);
    }

    [Fact]
    public void WriteToolResult_NewFileWithTrailingNewline_TrimsEmptyFinalLine()
    {
        // A trailing \n in `content` produces an extra empty split element that must not
        // leak into the hunk as a phantom `+` line (would otherwise show a blank added line).
        var registry = new Dictionary<string, ClaudeCodeOutputParser.ToolUseInfo>();
        var assistantEvt = Parse("""
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu_w","name":"Write",
              "input":{"file_path":"/tmp/a.txt","content":"a\nb\n"}}]}}
            """);
        ClaudeCodeOutputParser.ParseAssistantEvent(Guid.NewGuid(), assistantEvt, registry);

        var userEvt = Parse("""
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu_w","content":"ok"}]},
             "tool_use_result":{"type":"create","filePath":"/tmp/a.txt","content":"a\nb\n",
              "structuredPatch":[],"originalFile":null,"userModified":false}}
            """);
        var msgs = ClaudeCodeOutputParser.ParseUserEvent(Guid.NewGuid(), userEvt, registry);

        var change = Assert.Single(Assert.Single(msgs).FileChanges);
        Assert.Equal("@@ -0,0 +1,2 @@\n+a\n+b", change.Diff);
    }

    [Fact]
    public void EditToolResult_FailedWithNoToolUseResult_EmitsFileRowWithEmptyDiff()
    {
        // Permission denial or other failure: Claude emits is_error + no tool_use_result.
        // We still want the file row in the chat so the failure is visible.
        var registry = new Dictionary<string, ClaudeCodeOutputParser.ToolUseInfo>();
        var assistantEvt = Parse("""
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu_3","name":"Edit",
              "input":{"file_path":"/tmp/denied.txt","old_string":"a","new_string":"b"}}]}}
            """);
        ClaudeCodeOutputParser.ParseAssistantEvent(Guid.NewGuid(), assistantEvt, registry);

        var userEvt = Parse("""
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu_3",
              "is_error":true,"content":"Error: permission denied"}]}}
            """);
        var msgs = ClaudeCodeOutputParser.ParseUserEvent(Guid.NewGuid(), userEvt, registry);

        var fileMsg = Assert.Single(msgs);
        Assert.Equal(MessageStatus.Failed, fileMsg.Status);
        var change = Assert.Single(fileMsg.FileChanges);
        Assert.Equal("/tmp/denied.txt", change.Path);
        Assert.Equal(string.Empty, change.Diff);
    }

    [Fact]
    public void EditToolResult_MultiHunkPatch_EmitsOneFileChangePerHunk()
    {
        // When a single Edit (or MultiEdit) produces disjoint hunks, we emit one
        // FileChange row per hunk so the UI renders each block independently and
        // keeps the header line numbers correct.
        var registry = new Dictionary<string, ClaudeCodeOutputParser.ToolUseInfo>();
        var assistantEvt = Parse("""
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu_m","name":"Edit",
              "input":{"file_path":"/tmp/multi.txt","old_string":"x","new_string":"X"}}]}}
            """);
        ClaudeCodeOutputParser.ParseAssistantEvent(Guid.NewGuid(), assistantEvt, registry);

        var userEvt = Parse("""
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu_m","content":"ok"}]},
             "tool_use_result":{"filePath":"/tmp/multi.txt","oldString":"x","newString":"X",
              "originalFile":"x\na\nb\nx\n",
              "structuredPatch":[
                {"oldStart":1,"oldLines":1,"newStart":1,"newLines":1,"lines":["-x","+X"]},
                {"oldStart":4,"oldLines":1,"newStart":4,"newLines":1,"lines":["-x","+X"]}],
              "userModified":false,"replaceAll":false}}
            """);
        var msgs = ClaudeCodeOutputParser.ParseUserEvent(Guid.NewGuid(), userEvt, registry);

        var fileMsg = Assert.Single(msgs);
        var hunks = fileMsg.FileChanges.ToList();
        Assert.Equal(2, hunks.Count);
        Assert.Equal("@@ -1,1 +1,1 @@\n-x\n+X", hunks[0].Diff);
        Assert.Equal("@@ -4,1 +4,1 @@\n-x\n+X", hunks[1].Diff);
    }
}
