using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Verifies the engine collapses every agent's raw tool-result shape into a consistent list of
/// <see cref="ContentSegment"/>. Sample shapes are taken from real Claude / Codex / MCP results.
/// </summary>
public class ToolResultNormalizerTests
{
    [Fact]
    public void NullOrEmpty_ReturnsNoSegments()
    {
        Assert.Empty(ToolResultNormalizer.Parse(null));
        Assert.Empty(ToolResultNormalizer.Parse(""));
    }

    [Fact]
    public void PlainText_BecomesSingleTextSegment()
    {
        // Claude / ACP tool output is often just text (and web-search results, etc.).
        var seg = Assert.Single(ToolResultNormalizer.Parse("Web search results for query: \"SHEIN\""));
        Assert.Equal(ContentSegmentKind.Text, seg.Kind);
        Assert.Equal("Web search results for query: \"SHEIN\"", seg.Value);
    }

    [Fact]
    public void McpCallToolResult_ExtractsTextFromContentArray()
    {
        // Real MCP shape: { "content":[{"type":"text","text":"..."}], "structuredContent":..., "isError":... }
        var raw = """{"content":[{"type":"text","text":"hello"}],"structuredContent":{"x":1},"isError":false}""";
        var seg = Assert.Single(ToolResultNormalizer.Parse(raw));
        Assert.Equal(ContentSegmentKind.Text, seg.Kind);
        Assert.Equal("hello", seg.Value);
    }

    [Fact]
    public void ContentItemArray_TextAndInputText_BecomeText()
    {
        var raw = """[{"type":"inputText","text":"done"},{"type":"text","text":"more"}]""";
        var segs = ToolResultNormalizer.Parse(raw);
        Assert.Equal(2, segs.Count);
        Assert.All(segs, s => Assert.Equal(ContentSegmentKind.Text, s.Kind));
        Assert.Equal("done", segs[0].Value);
        Assert.Equal("more", segs[1].Value);
    }

    [Fact]
    public void ImageContentItems_ExtractUrls()
    {
        var raw = """[{"type":"inputImage","imageUrl":"https://x/a.png"},{"type":"image","source":{"url":"https://x/b.png"}}]""";
        var segs = ToolResultNormalizer.Parse(raw);
        Assert.Equal(2, segs.Count);
        Assert.Equal(new ContentSegment(ContentSegmentKind.Image, "https://x/a.png"), segs[0]);
        Assert.Equal(new ContentSegment(ContentSegmentKind.Image, "https://x/b.png"), segs[1]);
    }

    [Fact]
    public void ArbitraryJsonObject_BecomesPrettyJsonSegment()
    {
        // Real shape (a mintokei plan tool result) — no text/image structure → pretty-printed JSON.
        var raw = """{"planId":"16b12924-728d-44c2-8d82-d97eada42eb4","status":"New","name":"Example Web App Setup"}""";
        var seg = Assert.Single(ToolResultNormalizer.Parse(raw));
        Assert.Equal(ContentSegmentKind.Json, seg.Kind);
        Assert.Contains("\"planId\"", seg.Value);
        Assert.Contains("Example Web App Setup", seg.Value);
    }

    [Fact]
    public void ArrayOfArbitraryObjects_WithoutTypeField_FallsBackToJson()
    {
        // Real shape: [{ "id":..., "content":... }] — first element has no "type", so it is NOT a
        // content-item array; it renders as a single pretty-printed JSON segment.
        var raw = """[{"id":"098dcf2f","content":"Agent task for 'Check'"}]""";
        var seg = Assert.Single(ToolResultNormalizer.Parse(raw));
        Assert.Equal(ContentSegmentKind.Json, seg.Kind);
        Assert.Contains("098dcf2f", seg.Value);
    }

    [Fact]
    public void ToolCallData_ResultContent_DerivesFromResult_AndStaysConsistent()
    {
        var tc = new ToolCallData { ToolName = "read", Result = "plain output" };
        var seg = Assert.Single(tc.ResultContent);
        Assert.Equal(ContentSegmentKind.Text, seg.Kind);
        Assert.Equal("plain output", seg.Value);

        tc.Result = null;
        Assert.Empty(tc.ResultContent);
    }
}
