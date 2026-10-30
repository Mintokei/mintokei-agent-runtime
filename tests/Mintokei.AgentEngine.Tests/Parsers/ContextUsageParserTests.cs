using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mintokei.AgentEngine.Tests;

public class ContextUsageParserTests
{
    private static JsonElement Frame(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static ContextUsagePayload CodexUsage(string tokenUsage)
    {
        var parser = new CodexStreamParser(NullLogger.Instance, Guid.NewGuid());
        var outputs = parser.Consume(Frame(
            """{"method":"thread/tokenUsageUpdated","params":{"tokenUsage": """ + tokenUsage + "}}")).ToList();
        return Assert.IsType<ContextUsagePayload>(Assert.IsType<DeltaOutput>(Assert.Single(outputs)).Payload);
    }

    [Fact]
    public void Codex_PostCompactEstimate_UsesLastTotalWhenInputIsZero()
    {
        // Shape observed in a real Codex rollout immediately after compaction.
        var usage = CodexUsage("""
            {"last":{"inputTokens":0,"cachedInputTokens":0,"outputTokens":0,
                     "reasoningOutputTokens":0,"totalTokens":22915},
             "total":{"inputTokens":449541,"totalTokens":450783},"modelContextWindow":475000}
            """);

        Assert.Equal(22915, usage.UsedTokens);
        Assert.Equal(475000, usage.WindowTokens);
    }

    [Fact]
    public void Codex_IncludesOutput_WithoutDoubleCountingCachedOrReasoningTokens()
    {
        var usage = CodexUsage("""
            {"last":{"inputTokens":10000,"cachedInputTokens":8000,"outputTokens":2000,
                     "reasoningOutputTokens":1500,"totalTokens":12000},
             "total":{"totalTokens":950000},"modelContextWindow":258400}
            """);

        Assert.Equal(12000, usage.UsedTokens);
        Assert.Equal(258400, usage.WindowTokens);
    }

    [Fact]
    public void Codex_ExplicitZeroTotal_ResetsUsage()
    {
        var usage = CodexUsage("""{"last":{"inputTokens":0,"totalTokens":0}}""");
        Assert.Equal(0, usage.UsedTokens);
    }

    [Fact]
    public void Codex_MissingTotal_FallsBackToInputPlusOutput()
    {
        var usage = CodexUsage("""
            {"last":{"inputTokens":10000,"cachedInputTokens":8000,"outputTokens":2000,
                     "reasoningOutputTokens":1500}}
            """);
        Assert.Equal(12000, usage.UsedTokens);
    }

    [Fact]
    public void Codex_WindowOnly_DoesNotInventAUsedCount()
    {
        var usage = CodexUsage("""{"modelContextWindow":258400,"total":{"totalTokens":950000}}""");
        Assert.Null(usage.UsedTokens);
        Assert.Equal(258400, usage.WindowTokens);
    }

    [Fact]
    public void Codex_NullWindow_StillReportsUsage()
    {
        var usage = CodexUsage("""{"last":{"totalTokens":22915},"modelContextWindow":null}""");
        Assert.Equal(22915, usage.UsedTokens);
        Assert.Null(usage.WindowTokens);
    }

    [Theory]
    [InlineData("{\"type\":\"user\",\"isSynthetic\":true,\"message\":{\"content\":\"summary\"}}")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\"}")]
    public void Claude_CompactionRefreshesUsage_WithOrWithoutSyntheticSummary(string nextFrame)
    {
        var parser = new ClaudeStreamParser(NullLogger.Instance, Guid.NewGuid());
        var outputs = parser.Consume(Frame("""
            {"type":"system","subtype":"compact_boundary",
             "compact_metadata":{"trigger":"manual","pre_tokens":39388,"post_tokens":677}}
            """), false).ToList();
        outputs.AddRange(parser.Consume(Frame(nextFrame), false));

        var usage = Assert.Single(outputs.OfType<DeltaOutput>()
            .Select(o => o.Payload).OfType<ContextUsagePayload>());
        Assert.Equal(677, usage.UsedTokens);
        Assert.Null(usage.WindowTokens);
        Assert.Single(outputs.OfType<MessageOutput>(), o => o.Message.Type == MessageType.CompactBoundary);
    }

    [Fact]
    public void Claude_CompactionWithoutPostTokens_DoesNotInventAUsedCount()
    {
        var parser = new ClaudeStreamParser(NullLogger.Instance, Guid.NewGuid());
        var outputs = parser.Consume(Frame("""
            {"type":"system","subtype":"compact_boundary","compact_metadata":{"pre_tokens":39388}}
            """), false).ToList();
        Assert.Empty(outputs.OfType<DeltaOutput>());
    }
}
