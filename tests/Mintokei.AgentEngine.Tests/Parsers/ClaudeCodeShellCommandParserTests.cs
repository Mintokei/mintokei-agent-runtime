using System.Text.Json;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Verifies that shell tool calls from the Claude Code stream-json output map to the
/// unified <c>CommandExecution</c> shape rather than a generic <c>ToolCall</c>.
/// Both Bash and PowerShell (emitted by Claude Code on Windows runners) carry the same
/// <c>command</c> input field, so both must produce a CommandExecution message.
/// </summary>
public class ClaudeCodeShellCommandParserTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    public void ShellToolUse_ProducesCommandExecution(string toolName)
    {
        var registry = new Dictionary<string, ClaudeCodeOutputParser.ToolUseInfo>();
        var evt = Parse("""
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu_1","name":"__TOOL__",
              "input":{"command":"echo hi","description":"say hi"}}]}}
            """.Replace("__TOOL__", toolName));

        var msg = Assert.Single(ClaudeCodeOutputParser.ParseAssistantEvent(Guid.NewGuid(), evt, registry));

        Assert.Equal(MessageType.CommandExecution, msg.Type);
        Assert.Equal(MessageRole.Tool, msg.Role);
        Assert.Equal(MessageStatus.InProgress, msg.Status);
        Assert.NotNull(msg.CommandExecution);
        Assert.Equal("echo hi", msg.CommandExecution!.Command);
        Assert.Null(msg.ToolCall);
    }

    [Fact]
    public void PowerShellToolResult_ProducesCommandExecutionWithOutput()
    {
        var registry = new Dictionary<string, ClaudeCodeOutputParser.ToolUseInfo>();
        var assistantEvt = Parse("""
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu_ps","name":"PowerShell",
              "input":{"command":"Get-ChildItem"}}]}}
            """);
        ClaudeCodeOutputParser.ParseAssistantEvent(Guid.NewGuid(), assistantEvt, registry);

        var userEvt = Parse("""
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu_ps",
              "content":"Directory: C:\\repo\n\nName\n----\nsrc"}]}}
            """);
        var msg = Assert.Single(ClaudeCodeOutputParser.ParseUserEvent(Guid.NewGuid(), userEvt, registry));

        Assert.Equal(MessageType.CommandExecution, msg.Type);
        Assert.Equal(MessageStatus.Completed, msg.Status);
        Assert.NotNull(msg.CommandExecution);
        Assert.Equal("Get-ChildItem", msg.CommandExecution!.Command);
        Assert.Contains("Directory", msg.CommandExecution.Output);
        Assert.Null(msg.ToolCall);
    }
}
