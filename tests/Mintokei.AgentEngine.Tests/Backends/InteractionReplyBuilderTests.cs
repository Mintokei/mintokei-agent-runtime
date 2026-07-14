using System.Text.Json;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Covers the keyed durable-recovery reply builders: that each rebuilds the correct wire reply
/// from a persisted interaction + its ReplyContext, and returns null when it can't.
/// </summary>
public class InteractionReplyBuilderTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Claude_RebuildsControlResponseFromExistingColumns()
    {
        var interaction = new UserInteractionData
        {
            RequestId = "req-1",
            ToolName = "Bash",
            ToolInput = "{\"command\":\"ls\"}",
        };

        var reply = new ClaudeInteractionReplyBuilder().Build(interaction, new UserInteractionResponse("allow", null, null));

        var root = Parse(Assert.IsType<string>(reply));
        Assert.Equal("control_response", root.GetProperty("type").GetString());
    }

    [Fact]
    public void Codex_Approval_MapsAllowToAccept_AndEchoesNumericId()
    {
        var interaction = new UserInteractionData
        {
            RequestId = "7",
            ReplyContext = CodexInteractionReplyBuilder.ApprovalContext,
        };

        var root = Parse(new CodexInteractionReplyBuilder().Build(interaction, new UserInteractionResponse("allow", null, null))!);

        Assert.Equal(7, root.GetProperty("id").GetInt32());
        Assert.Equal("accept", root.GetProperty("result").GetProperty("decision").GetString());
    }

    [Fact]
    public void Codex_UserInput_EchoesAnswers()
    {
        var interaction = new UserInteractionData
        {
            RequestId = "3",
            ReplyContext = CodexInteractionReplyBuilder.UserInputContext,
        };

        var root = Parse(new CodexInteractionReplyBuilder().Build(
            interaction, new UserInteractionResponse("allow", null, """{"q1":{"answers":["yes"]}}"""))!);

        Assert.Equal("yes", root.GetProperty("result").GetProperty("answers").GetProperty("q1").GetProperty("answers")[0].GetString());
    }

    [Fact]
    public void Codex_Elicitation_MapsToAcceptAction()
    {
        var interaction = new UserInteractionData
        {
            RequestId = "9",
            ReplyContext = CodexInteractionReplyBuilder.ElicitationContext,
        };

        var root = Parse(new CodexInteractionReplyBuilder().Build(interaction, new UserInteractionResponse("allow", null, null))!);

        Assert.Equal("accept", root.GetProperty("result").GetProperty("action").GetString());
    }

    [Fact]
    public void Codex_MissingContext_ReturnsNull()
    {
        var interaction = new UserInteractionData { RequestId = "1" };
        Assert.Null(new CodexInteractionReplyBuilder().Build(interaction, new UserInteractionResponse("allow", null, null)));
    }

    [Fact]
    public void Acp_ResolvesOptionId_AndEchoesNumericId()
    {
        var options = Parse("""[{"optionId":"opt-allow-once","kind":"allow_once"}]""");
        var interaction = new UserInteractionData
        {
            RequestId = "5",
            ReplyContext = AcpInteractionReplyBuilder.BuildContext(options, useIntId: true),
        };

        var root = Parse(new AcpInteractionReplyBuilder().Build(interaction, new UserInteractionResponse("allow", null, null))!);

        Assert.Equal(5, root.GetProperty("id").GetInt32());
        Assert.Equal("opt-allow-once", root.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public void Acp_EchoesStringId_WhenUseIntIdFalse()
    {
        var options = Parse("""[{"optionId":"opt-allow-once","kind":"allow_once"}]""");
        var interaction = new UserInteractionData
        {
            RequestId = "abc",
            ReplyContext = AcpInteractionReplyBuilder.BuildContext(options, useIntId: false),
        };

        var root = Parse(new AcpInteractionReplyBuilder().Build(interaction, new UserInteractionResponse("allow", null, null))!);

        Assert.Equal(JsonValueKind.String, root.GetProperty("id").ValueKind);
        Assert.Equal("abc", root.GetProperty("id").GetString());
    }

    [Fact]
    public void Acp_MissingContext_ReturnsNull()
    {
        var interaction = new UserInteractionData { RequestId = "5" };
        Assert.Null(new AcpInteractionReplyBuilder().Build(interaction, new UserInteractionResponse("allow", null, null)));
    }
}
