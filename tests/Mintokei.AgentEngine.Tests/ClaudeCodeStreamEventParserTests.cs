using System.Text.Json;

namespace Mintokei.AgentEngine.Tests;

public class ClaudeCodeStreamEventParserTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ParseStreamEvent_MessageStart_ReturnsTurnStart()
    {
        var root = Parse("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_1"}}}""");
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root);

        var turn = Assert.IsType<TurnPayload>(payload);
        Assert.True(turn.IsStart);
    }

    [Fact]
    public void ParseStreamEvent_MessageStop_ReturnsNull()
    {
        // `message_stop` is per-Anthropic-message and fires multiple times per
        // user turn (once per LLM request). The user-visible turn_stop delta is
        // emitted by ClaudeCodeExecutionService at the CLI `result` event.
        var root = Parse("""{"type":"stream_event","event":{"type":"message_stop"}}""");
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root);

        Assert.Null(payload);
    }

    [Fact]
    public void ParseStreamEvent_ContentBlockStart_Text_ReturnsBlockStart()
    {
        var root = Parse("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}}""");
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root);

        var block = Assert.IsType<BlockStartPayload>(payload);
        Assert.Equal(0, block.BlockIndex);
        Assert.Equal("text", block.BlockType);
        Assert.Null(block.ToolName);
    }

    [Fact]
    public void ParseStreamEvent_ContentBlockStart_Thinking_ReturnsMappedBlockStart()
    {
        var root = Parse("""{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"thinking","thinking":""}}}""");
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root);

        var block = Assert.IsType<BlockStartPayload>(payload);
        Assert.Equal(1, block.BlockIndex);
        Assert.Equal("reasoning", block.BlockType);
    }

    [Fact]
    public void ParseStreamEvent_ContentBlockStart_ToolUse_IncludesToolName()
    {
        var root = Parse("""{"type":"stream_event","event":{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"tu_1","name":"Bash","input":{}}}}""");
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root);

        var block = Assert.IsType<BlockStartPayload>(payload);
        Assert.Equal(2, block.BlockIndex);
        Assert.Equal("tool_use", block.BlockType);
        Assert.Equal("Bash", block.ToolName);
    }

    [Fact]
    public void ParseStreamEvent_TextDelta_ReturnsContentDelta()
    {
        var root = Parse("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}}""");
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root);

        var delta = Assert.IsType<ContentDeltaPayload>(payload);
        Assert.Equal("text", delta.DeltaType);
        Assert.Equal(0, delta.BlockIndex);
        Assert.Equal("Hello", delta.Delta);
    }

    [Fact]
    public void ParseStreamEvent_ThinkingDelta_ReturnsReasoningDelta()
    {
        var root = Parse("""{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"thinking_delta","thinking":"Let me think"}}}""");
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root);

        var delta = Assert.IsType<ContentDeltaPayload>(payload);
        Assert.Equal("reasoning", delta.DeltaType);
        Assert.Equal(1, delta.BlockIndex);
        Assert.Equal("Let me think", delta.Delta);
    }

    [Fact]
    public void ParseStreamEvent_InputJsonDelta_ReturnsToolInputDelta()
    {
        var root = Parse("""{"type":"stream_event","event":{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"command\":\"ls\""}}}""");
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root);

        var delta = Assert.IsType<ContentDeltaPayload>(payload);
        Assert.Equal("tool_input", delta.DeltaType);
        Assert.Equal(2, delta.BlockIndex);
        Assert.Equal("{\"command\":\"ls\"", delta.Delta);
    }

    [Fact]
    public void ParseStreamEvent_ContentBlockStop_ReturnsBlockStop()
    {
        var root = Parse("""{"type":"stream_event","event":{"type":"content_block_stop","index":0}}""");
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root);

        var block = Assert.IsType<BlockStopPayload>(payload);
        Assert.Equal(0, block.BlockIndex);
    }

    [Fact]
    public void ParseStreamEvent_UnknownEventType_ReturnsNull()
    {
        var root = Parse("""{"type":"stream_event","event":{"type":"ping"}}""");
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root);

        Assert.Null(payload);
    }

    [Fact]
    public void ParseStreamEvent_UnknownDeltaType_ReturnsNull()
    {
        var root = Parse("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"unknown_delta","data":"x"}}}""");
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root);

        Assert.Null(payload);
    }

    [Fact]
    public void ParseStreamEvent_MissingEventProperty_ReturnsNull()
    {
        var root = Parse("""{"type":"stream_event"}""");
        var payload = ClaudeCodeOutputParser.ParseStreamEvent(root);

        Assert.Null(payload);
    }

    [Fact]
    public void ParseStreamEvent_MultiCycleTurn_ProducesNoTurnStop()
    {
        // Regression: a single user turn with tool calls produces multiple
        // `message_start`/`message_stop` pairs (one per LLM request/response).
        // None of the `message_stop` events should map to a turn_stop — that
        // boundary is owned by the execution service's `result` handler.
        string[] stream =
        [
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_stop","index":0}}""",
            """{"type":"stream_event","event":{"type":"message_stop"}}""",
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"m2"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}}""",
            """{"type":"stream_event","event":{"type":"message_stop"}}""",
        ];

        var payloads = stream.Select(Parse).Select(e => ClaudeCodeOutputParser.ParseStreamEvent(e)).ToList();

        var turnStarts = payloads.OfType<TurnPayload>().Count(p => p.IsStart);
        var turnStops = payloads.OfType<TurnPayload>().Count(p => !p.IsStart);
        Assert.Equal(2, turnStarts);
        Assert.Equal(0, turnStops);
    }
}
