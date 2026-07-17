namespace Mintokei.AgentEngine.Tests;

public class ClaudeCodeModelDisplayNameTests
{
    [Theory]
    [InlineData("default", "Default (recommended)", "Opus 4.7 with 1M context · Most capable for complex work", "Default (Opus 4.7 with 1M context)")]
    [InlineData("sonnet", "Sonnet", "Sonnet 4.6 · Best for everyday tasks", "Sonnet 4.6")]
    [InlineData("sonnet[1m]", "Sonnet (1M context)", "Sonnet 4.6 with 1M context · Billed as extra usage · $3/$15 per Mtok", "Sonnet 4.6 with 1M context")]
    [InlineData("haiku", "Haiku", "Haiku 4.5 · Fastest for quick answers", "Haiku 4.5")]
    public void ResolveDisplayName_PromotesModelNameFromDescription(
        string id, string original, string description, string expected)
    {
        var resolved = ClaudeCodeModelDiscoveryProvider.ResolveDisplayName(id, original, description);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveDisplayName_NullDescription_ReturnsOriginal()
    {
        var resolved = ClaudeCodeModelDiscoveryProvider.ResolveDisplayName("opus", "Opus", description: null);

        Assert.Equal("Opus", resolved);
    }

    [Fact]
    public void ResolveDisplayName_DescriptionWithoutModelFamily_ReturnsOriginal()
    {
        var resolved = ClaudeCodeModelDiscoveryProvider.ResolveDisplayName(
            "custom", "Custom", "Some marketing tagline · with extras");

        Assert.Equal("Custom", resolved);
    }

    [Fact]
    public void ResolveDisplayName_DescriptionMissingDigit_ReturnsOriginal()
    {
        var resolved = ClaudeCodeModelDiscoveryProvider.ResolveDisplayName(
            "sonnet", "Sonnet", "Sonnet · no version here");

        Assert.Equal("Sonnet", resolved);
    }

    [Fact]
    public void CuratedModels_IncludeFableAndMythos()
    {
        Assert.Contains(
            ClaudeCodeModelDiscoveryProvider.CuratedModels,
            model => model.Id == "claude-fable-5" && model.DisplayName == "Claude Fable 5");
        Assert.Contains(
            ClaudeCodeModelDiscoveryProvider.CuratedModels,
            model => model.Id == "claude-mythos-5" && model.DisplayName == "Claude Mythos 5");
    }
}
