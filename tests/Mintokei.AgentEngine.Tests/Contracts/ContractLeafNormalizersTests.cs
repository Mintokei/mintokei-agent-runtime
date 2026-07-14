using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Verifies the additive normalized views over the remaining raw-JSON leaves on the engine
/// contract — tool arguments, attached images, and the pre-compaction tool list. Sample shapes
/// are taken from real prod rows.
/// </summary>
public class ContractLeafNormalizersTests
{
    // ── ToolArgument ──

    [Fact]
    public void ToolArguments_FlatObject_BecomesKeyValuePairs()
    {
        // Real shape: {"query":"…","max_results":1}
        var args = ToolArgumentNormalizer.Parse("""{"query":"Claude AI news 2026","max_results":1}""");
        Assert.Equal(2, args.Count);
        Assert.Equal(new ToolArgument("query", "Claude AI news 2026"), args[0]);
        Assert.Equal(new ToolArgument("max_results", "1"), args[1]);
    }

    [Fact]
    public void ToolArguments_SkipsNestedAndNullValues()
    {
        var args = ToolArgumentNormalizer.Parse("""{"path":"a.ts","flag":true,"nested":{"x":1},"list":[1,2],"empty":null}""");
        Assert.Collection(args,
            a => Assert.Equal(new ToolArgument("path", "a.ts"), a),
            a => Assert.Equal(new ToolArgument("flag", "true"), a));
    }

    [Fact]
    public void ToolArguments_NonObjectOrInvalid_IsEmpty()
    {
        Assert.Empty(ToolArgumentNormalizer.Parse(null));
        Assert.Empty(ToolArgumentNormalizer.Parse(""));
        Assert.Empty(ToolArgumentNormalizer.Parse("[1,2,3]"));
        Assert.Empty(ToolArgumentNormalizer.Parse("not json"));
    }

    [Fact]
    public void ToolCallData_ArgumentPairs_DerivesFromArguments()
    {
        var tc = new ToolCallData { ToolName = "WebSearch", Arguments = """{"query":"x"}""" };
        Assert.Equal(new ToolArgument("query", "x"), Assert.Single(tc.ArgumentPairs));
    }

    // ── ImageAttachment ──

    [Fact]
    public void Images_Base64DataUri_IsExtracted()
    {
        // Real shape: [{"type":"base64","data":"data:image/png;base64,…"}]
        var raw = """[{"type":"base64","data":"data:image/png;base64,AAAA"},{"type":"base64","data":"data:image/jpeg;base64,BBBB"}]""";
        var images = ImageNormalizer.Parse(raw);
        Assert.Equal(2, images.Count);
        Assert.Equal("data:image/png;base64,AAAA", images[0].Data);
        Assert.Equal("data:image/jpeg;base64,BBBB", images[1].Data);
    }

    [Fact]
    public void Images_FallsBackToUrl_AndSkipsEntriesWithNeither()
    {
        var raw = """[{"url":"https://x/a.png"},{"type":"base64"}]""";
        Assert.Equal("https://x/a.png", Assert.Single(ImageNormalizer.Parse(raw)).Data);
    }

    [Fact]
    public void AgentMessage_Images_DerivesFromImagesJson()
    {
        var msg = new AgentMessage { ImagesJson = """[{"type":"base64","data":"data:image/png;base64,ZZ"}]""" };
        Assert.Equal("data:image/png;base64,ZZ", Assert.Single(msg.Images).Data);
    }

    // ── ToolsBefore (string array) ──

    [Fact]
    public void ToolsBefore_StringArray_IsExtracted()
    {
        // Real shape: ["TaskStop","mcp__…"]
        var tools = JsonStringArray.Parse("""["TaskStop","mcp__mintokei__create_agent","WebFetch"]""");
        Assert.Equal(new[] { "TaskStop", "mcp__mintokei__create_agent", "WebFetch" }, tools);
    }

    [Fact]
    public void CompactBoundaryData_ToolsBefore_DerivesFromJson()
    {
        var cb = new CompactBoundaryData { ToolsBeforeJson = """["Read","Bash"]""" };
        Assert.Equal(new[] { "Read", "Bash" }, cb.ToolsBefore);
        Assert.Empty(new CompactBoundaryData().ToolsBefore);
    }
}
