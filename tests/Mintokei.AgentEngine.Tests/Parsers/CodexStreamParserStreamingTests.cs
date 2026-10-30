using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

public sealed class CodexStreamParserStreamingTests
{
    private readonly CodexStreamParser parser = new(NullLogger.Instance, Guid.NewGuid());
    private AgentStreamOutput[] Parse(string json) => parser.Consume(JsonDocument.Parse(json).RootElement).ToArray();
    private AgentStreamOutput[] Delta(string id, string text) => Parse(JsonSerializer.Serialize(new {
        method = "item/agentMessage/delta", @params = new { itemId = id, delta = text, threadId = "thread", turnId = "turn" }
    }));
    private AgentStreamOutput[] Complete(string id, string text) => Parse(JsonSerializer.Serialize(new {
        method = "item/completed", @params = new { item = new { type = "agentMessage", id, text } }
    }));

    [Fact]
    public void Text_is_emitted_before_item_completed_and_chunks_keep_the_same_block()
    {
        var first = Delta("answer", "Hello ");
        var start = Assert.IsType<BlockStartPayload>(Assert.IsType<DeltaOutput>(first[0]).Payload);
        var text = Assert.IsType<ContentDeltaPayload>(Assert.IsType<DeltaOutput>(first[1]).Payload);
        Assert.Equal("text", start.BlockType);
        Assert.Equal("text", text.DeltaType);
        Assert.Equal(start.BlockIndex, text.BlockIndex);
        Assert.Equal("Hello ", text.Delta);
        var second = Assert.IsType<ContentDeltaPayload>(Assert.IsType<DeltaOutput>(Assert.Single(Delta("answer", "world"))).Payload);
        Assert.Equal(start.BlockIndex, second.BlockIndex);
        Assert.Equal("world", second.Delta);
        Assert.Empty(first.OfType<MessageOutput>());
    }

    [Fact]
    public void Completion_persists_the_authoritative_message_once_and_clears_replay_preview()
    {
        Delta("answer", "Partial");
        var outputs = Complete("answer", "Full authoritative text");
        Assert.IsType<BlockStopPayload>(Assert.IsType<DeltaOutput>(outputs[0]).Payload);
        var message = Assert.Single(outputs.OfType<MessageOutput>()).Message;
        Assert.Equal("Full authoritative text", message.Content);
        Assert.Equal("answer", message.ExternalId);
        Assert.IsType<ClearDeltaSnapshot>(outputs[^1]);
        var next = Assert.IsType<BlockStartPayload>(Assert.IsType<DeltaOutput>(Delta("next", "Next answer")[0]).Payload);
        Assert.Equal(1, next.BlockIndex);
    }

    [Fact]
    public void Completion_without_deltas_keeps_existing_non_streaming_behavior()
    {
        Assert.IsType<MessageOutput>(Assert.Single(Complete("answer", "Full text")));
    }

    [Theory]
    [InlineData("interrupted")]
    [InlineData("failed")]
    public void Unfinished_text_is_flushed_before_terminal_message_and_turn_end(string status)
    {
        Delta("answer", "Keep this partial text");
        var outputs = Parse(JsonSerializer.Serialize(new { method = "turn/completed", @params = new { turn = new { status } } }));
        Assert.IsType<FlushDeltaSnapshot>(outputs[0]);
        Assert.IsType<TurnEnded>(outputs[^1]);
        var firstNextTurn = Delta("answer", "Fresh text");
        Assert.IsType<BlockStartPayload>(Assert.IsType<DeltaOutput>(firstNextTurn[0]).Payload);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"itemId\":\"a\"}")]
    [InlineData("{\"itemId\":\"a\",\"delta\":null}")]
    [InlineData("{\"itemId\":\"a\",\"delta\":\"\"}")]
    [InlineData("{\"itemId\":null,\"delta\":\"text\"}")]
    public void Malformed_or_empty_deltas_are_ignored(string parameters)
    {
        Assert.Empty(Parse("{\"method\":\"item/agentMessage/delta\",\"params\":" + parameters + "}"));
    }

    [Fact]
    public void Legacy_duplicate_notifications_are_not_forwarded_twice()
    {
        Delta("answer", "hello");
        Assert.Empty(Parse("""{"method":"codex/event/agent_message_delta","params":{"delta":"hello"}}"""));
    }
}
