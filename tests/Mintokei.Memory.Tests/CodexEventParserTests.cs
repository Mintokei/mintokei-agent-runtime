using System.Text.Json.Nodes;

namespace Mintokei.Memory.Tests;
public class CodexEventParserTests
{
    // Minimal fixture matching actual codex-cli 0.164.0 web-search stdout.
    private const string WebSearch = """
        {"type":"item.completed","item":{"id":"item_0","type":"web_search","id":"exec-search","query":"official version","action":{"type":"search","query":"official version"},"results":[{"url":"https://example.com","ref_id":"turn0search0"}]}}
        """;
    [Fact]
    public void Duplicate_ids_survive_result_round_trip_and_tool_message_processing()
    {
        var original = JsonNode.Parse(WebSearch)!;
        Assert.Throws<ArgumentException>(() => original["item"]!["type"]);
        var parsed = CodexEventParser.Parse(WebSearch);
        var execution = new LlmResult("A real response", [parsed.Event, parsed.Diagnostic], "completed", 1, null, "low", null, null, null, 0, null, "thread", "codex-cli 0.164.0");
        var result = Mintokei.Memory.Json.Read<LlmResult>(Mintokei.Memory.Json.Write(execution));
        var tool = Assert.Single(result.RawEvents, e => e?["type"]?.ToString() == "item.completed" && e?["item"]?["type"]?.ToString() == "web_search");
        Assert.Equal("exec-search", tool!["item"]!["id"]!.ToString());
        Assert.Equal("turn0search0", tool["item"]!["results"]![0]!["ref_id"]!.ToString());
        Assert.Equal(WebSearch, result.RawEvents[1]!["rawLine"]!.ToString());
        Assert.Equal("/item/id", result.RawEvents[1]!["duplicatePropertyPaths"]![0]!.ToString());
        Assert.Equal("A real response", result.Output);
    }

    [Fact]
    public void Normal_events_keep_their_data_without_diagnostics()
    {
        const string line = """{"type":"turn.completed","usage":{"input_tokens":12,"output_tokens":3},"extra":[null,true,"x",1.25]}""";
        var parsed = CodexEventParser.Parse(line);
        Assert.Null(parsed.Diagnostic);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(line), parsed.Event));
    }

    [Fact]
    public void Nested_duplicates_are_reported_with_escaped_paths_and_original_values_preserved()
    {
        const string line = """{"type":"event","items":[{"a/b~c":1,"a/b~c":2}]}""";
        var parsed = CodexEventParser.Parse(line);
        Assert.Equal(2, parsed.Event["items"]![0]!["a/b~c"]!.GetValue<int>());
        Assert.Equal("/items/0/a~1b~0c", parsed.Diagnostic!["duplicatePropertyPaths"]![0]!.ToString());
        Assert.Equal(line, parsed.Diagnostic["rawLine"]!.ToString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    public void Non_events_are_preserved_as_unparsed_text(string line)
    {
        var parsed = CodexEventParser.Parse(line);
        Assert.Equal("unparsed", parsed.Event["type"]!.ToString());
        Assert.Equal(line, parsed.Event["text"]!.ToString());
    }
}
