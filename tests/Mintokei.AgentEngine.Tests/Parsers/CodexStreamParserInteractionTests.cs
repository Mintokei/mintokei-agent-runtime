using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Locks the bidirectional frames the Codex parser now emits as pure data (pump unification):
/// the three server-originated requests → <see cref="InteractionRequested"/> (with a
/// <c>CacheKey</c> for elicitation) and JSON-RPC responses → <see cref="ControlResponseReceived"/>.
/// The reply serialization + MCP cache behaviour live in the dispatch.
/// </summary>
public class CodexStreamParserInteractionTests
{
    private static JsonElement Frame(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<AgentStreamOutput> Parse(string json)
        => new CodexStreamParser(NullLogger.Instance, Guid.NewGuid()).Consume(Frame(json)).ToList();

    [Fact]
    public void CommandApproval_YieldsInteractionRequested()
    {
        var q = Assert.IsType<InteractionRequested>(Assert.Single(Parse(
            """{"id":1,"method":"item/commandExecution/requestApproval","params":{"command":"ls","cwd":"/tmp","reason":"need approval"}}""")));

        Assert.Equal("1", q.RequestId);
        Assert.Null(q.CacheKey);
        Assert.Equal(MessageType.PermissionRequest, q.Message.Type);
        Assert.Equal("need approval", q.Message.Content);
        Assert.Equal("ls", q.Message.UserInteraction!.Command);
        Assert.Equal("/tmp", q.Message.UserInteraction.Cwd);
        Assert.Equal(CodexInteractionReplyBuilder.ApprovalContext, q.Message.UserInteraction.ReplyContext);
        Assert.Equal("Shell", q.NotifyToolName);
    }

    [Fact]
    public void FileChangeApproval_UsesFallbackContent()
    {
        var q = Assert.IsType<InteractionRequested>(Assert.Single(Parse(
            """{"id":2,"method":"item/fileChange/requestApproval","params":{}}""")));

        Assert.Equal("File change approval requested", q.Message.Content);
        Assert.Equal("FileChange", q.NotifyToolName);
    }

    [Fact]
    public void UserInput_YieldsUserQuestion()
    {
        var q = Assert.IsType<InteractionRequested>(Assert.Single(Parse(
            """{"id":3,"method":"item/tool/requestUserInput","params":{"questions":[{"question":"Pick?"}]}}""")));

        Assert.Equal(MessageType.UserQuestion, q.Message.Type);
        Assert.Contains("Pick?", q.Message.UserInteraction!.Questions);
        Assert.Equal(CodexInteractionReplyBuilder.UserInputContext, q.Message.UserInteraction.ReplyContext);
    }

    [Fact]
    public void McpElicitation_SetsCacheKey_AndElicitationContext()
    {
        var q = Assert.IsType<InteractionRequested>(Assert.Single(Parse(
            """{"id":4,"method":"mcpServer/elicitation/request","params":{"serverName":"srv","message":"run tool \"mytool\"","_meta":{"tool_description":"desc","persist":"session"}}}""")));

        Assert.Equal("srv:mytool", q.CacheKey);                     // serverName:toolName → dispatch's cache
        Assert.Equal("srv", q.Message.UserInteraction!.ToolName);
        Assert.Equal("desc", q.Message.UserInteraction.Reason);
        Assert.Equal(CodexInteractionReplyBuilder.ElicitationContext, q.Message.UserInteraction.ReplyContext);
        Assert.Contains("mcpSessionScope", q.Message.UserInteraction.Suggestions);
    }

    [Fact]
    public void McpElicitation_NoDerivableToolName_HasNullCacheKey()
    {
        var q = Assert.IsType<InteractionRequested>(Assert.Single(Parse(
            """{"id":5,"method":"mcpServer/elicitation/request","params":{"serverName":"srv","message":"no tool name here"}}""")));

        Assert.Null(q.CacheKey);
    }

    [Fact]
    public void Response_YieldsControlResponseReceived_WithId()
    {
        var r = Assert.IsType<ControlResponseReceived>(Assert.Single(Parse("""{"id":6,"result":{"ok":true}}""")));
        Assert.Equal("6", r.Id);
    }
}
