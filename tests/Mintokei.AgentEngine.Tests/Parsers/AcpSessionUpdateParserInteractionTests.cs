using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Locks the bidirectional frames the ACP parser now emits as pure data (pump unification):
/// <c>session/request_permission</c> → <see cref="InteractionRequested"/> (options + id-kind stashed
/// in ReplyContext for the dispatch) and responses → <see cref="ControlResponseReceived"/>, while
/// <c>session/update</c> still routes to the one-way transcript path.
/// </summary>
public class AcpSessionUpdateParserInteractionTests
{
    private static JsonElement Frame(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<AgentStreamOutput> Parse(string json)
        => new AcpSessionUpdateParser(NullLogger.Instance).Parse(Guid.NewGuid(), Frame(json)).ToList();

    [Fact]
    public void PermissionRequest_Execute_YieldsInteractionRequested()
    {
        var q = Assert.IsType<InteractionRequested>(Assert.Single(Parse(
            """
            {"id":1,"method":"session/request_permission","params":{
              "toolCall":{"toolCallId":"tc1","title":"Run ls","kind":"execute","rawInput":{"command":"ls"}},
              "options":[{"optionId":"opt-allow","kind":"allow_once"}]}}
            """)));

        Assert.Equal("1", q.RequestId);
        Assert.Null(q.CacheKey);
        Assert.Equal(MessageType.PermissionRequest, q.Message.Type);
        Assert.Equal("ls", q.Message.Content);                          // shell tool → command
        Assert.Equal("Run ls", q.Message.UserInteraction!.ToolName);
        Assert.Equal("ls", q.Message.UserInteraction.Command);
        Assert.Contains("opt-allow", q.Message.UserInteraction.ReplyContext);   // offered options stashed
        Assert.Contains("useIntId", q.Message.UserInteraction.ReplyContext);    // id-kind stashed
        Assert.Equal("Run ls", q.NotifyToolName);
    }

    [Fact]
    public void PermissionRequest_NonExecute_UsesTitleContent_AndNoCommand()
    {
        var q = Assert.IsType<InteractionRequested>(Assert.Single(Parse(
            """{"id":"req-2","method":"session/request_permission","params":{"toolCall":{"title":"Read file","kind":"read"},"options":[]}}""")));

        Assert.Equal("Read file", q.Message.Content);
        Assert.Null(q.Message.UserInteraction!.Command);
    }

    [Fact]
    public void Response_YieldsControlResponseReceived_WithId()
    {
        var r = Assert.IsType<ControlResponseReceived>(Assert.Single(Parse("""{"id":3,"result":{"ok":true}}""")));
        Assert.Equal("3", r.Id);
    }

    [Fact]
    public void UnhandledAgentRequest_YieldsNothing()
    {
        Assert.Empty(Parse("""{"id":4,"method":"fs/read_text_file","params":{}}"""));
    }

    [Fact]
    public void SessionUpdate_TextChunk_RoutesToOneWayDelta()
    {
        var outputs = Parse(
            """{"method":"session/update","params":{"update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hi"}}}}""");

        Assert.Contains(outputs, o => o is DeltaOutput);
    }
}
