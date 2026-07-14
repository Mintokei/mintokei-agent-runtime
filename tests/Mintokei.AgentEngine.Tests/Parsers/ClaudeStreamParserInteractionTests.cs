using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Locks the bidirectional frames the Claude parser now emits as pure data (pump unification):
/// <c>control_request/can_use_tool</c> → <see cref="InteractionRequested"/> and
/// <c>control_response</c> → <see cref="ControlResponseReceived"/>. The reply serialization itself
/// is covered by the reply-builder golden tests.
/// </summary>
public class ClaudeStreamParserInteractionTests
{
    private static JsonElement Frame(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<AgentStreamOutput> Parse(string json)
        => new ClaudeStreamParser(NullLogger.Instance, Guid.NewGuid())
            .Consume(Frame(json), isInterrupted: false).ToList();

    [Fact]
    public void ControlRequest_CanUseTool_Permission_YieldsInteractionRequested()
    {
        var outputs = Parse("""
            {"type":"control_request","request_id":"req-1","request":{
              "subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls"},
              "permission_suggestions":[{"type":"addRule"}],"decision_reason":"needs approval"}}
            """);

        var q = Assert.IsType<InteractionRequested>(Assert.Single(outputs));
        Assert.Equal("req-1", q.RequestId);
        Assert.Null(q.CacheKey);                       // Claude has no MCP session cache
        Assert.Equal(MessageType.PermissionRequest, q.Message.Type);
        Assert.Equal("req-1", q.Message.UserInteraction!.RequestId);
        Assert.Equal("Bash", q.Message.UserInteraction.ToolName);
        Assert.Contains("ls", q.Message.UserInteraction.ToolInput);
        Assert.Equal("needs approval", q.Message.UserInteraction.Reason);
        Assert.Contains("addRule", q.Message.UserInteraction.Suggestions);
        Assert.Equal("Bash", q.NotifyToolName);
    }

    [Fact]
    public void ControlRequest_AskUserQuestion_YieldsUserQuestionInteraction()
    {
        var outputs = Parse("""
            {"type":"control_request","request_id":"req-2","request":{
              "subtype":"can_use_tool","tool_name":"AskUserQuestion",
              "input":{"questions":[{"question":"Pick one?"}]}}}
            """);

        var q = Assert.IsType<InteractionRequested>(Assert.Single(outputs));
        Assert.Equal(MessageType.UserQuestion, q.Message.Type);
        Assert.Equal("Pick one?", q.Message.Content);
        Assert.Contains("Pick one?", q.Message.UserInteraction!.Questions);
    }

    [Fact]
    public void ControlResponse_YieldsControlResponseReceived_WithId()
    {
        var outputs = Parse("""{"type":"control_response","response":{"request_id":"req-3","subtype":"success"}}""");

        var r = Assert.IsType<ControlResponseReceived>(Assert.Single(outputs));
        Assert.Equal("req-3", r.Id);
    }

    [Fact]
    public void ControlRequest_NonCanUseTool_YieldsNothing()
    {
        var outputs = Parse("""{"type":"control_request","request_id":"req-4","request":{"subtype":"initialize"}}""");
        Assert.Empty(outputs);
    }
}
