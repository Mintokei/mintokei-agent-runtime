using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Tests for OpenCodeCliConfigMapper — argument-list construction must put
/// <c>acp</c> first (it's a subcommand, not a flag), and only emit
/// <c>--model</c> / <c>--agent</c> when their values are non-empty.
/// </summary>
public class OpenCodeCliConfigMapperTests
{
    [Fact]
    public void Map_ExtractsModelAndAgent()
    {
        var mapped = OpenCodeCliConfigMapper.Map(new Dictionary<string, string?>
        {
            ["model"] = "opencode/gpt-5-nano",
            ["agent"] = "build",
        });

        Assert.Equal("opencode/gpt-5-nano", mapped.Model);
        Assert.Equal("build", mapped.Agent);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void Map_DangerouslySkipPermissions_Truthy(string? value, bool expected)
    {
        var mapped = OpenCodeCliConfigMapper.Map(new Dictionary<string, string?>
        {
            ["dangerouslySkipPermissions"] = value,
        });

        Assert.Equal(expected, mapped.DangerouslySkipPermissions);
    }

    [Fact]
    public void ToArgumentList_PutsAcpSubcommandFirst()
    {
        var args = OpenCodeCliConfigMapper.ToArgumentList(
            new OpenCodeCliConfigMapper.MappedConfig(),
            cwd: "/work/dir");

        Assert.Equal("acp", args[0]);
    }

    [Fact]
    public void ToArgumentList_IncludesCwdWhenProvided()
    {
        var args = OpenCodeCliConfigMapper.ToArgumentList(
            new OpenCodeCliConfigMapper.MappedConfig(),
            cwd: "/work/dir");

        var cwdIdx = args.IndexOf("--cwd");
        Assert.True(cwdIdx > 0);
        Assert.Equal("/work/dir", args[cwdIdx + 1]);
    }

    [Fact]
    public void ToArgumentList_OmitsCwdWhenNullOrEmpty()
    {
        var args = OpenCodeCliConfigMapper.ToArgumentList(
            new OpenCodeCliConfigMapper.MappedConfig(),
            cwd: null);

        Assert.DoesNotContain("--cwd", args);

        var args2 = OpenCodeCliConfigMapper.ToArgumentList(
            new OpenCodeCliConfigMapper.MappedConfig(),
            cwd: "");
        Assert.DoesNotContain("--cwd", args2);
    }

    [Fact]
    public void ToArgumentList_DoesNotIncludeModelOrAgentFlags()
    {
        // opencode acp doesn't accept --model or --agent at the CLI level.
        // Model is sent via _meta.opencode.modelId on each session/prompt; see
        // OpenCodeCliExecutionService.BuildPromptParams. Agent profile is not
        // exposed by acp today.
        var args = OpenCodeCliConfigMapper.ToArgumentList(
            new OpenCodeCliConfigMapper.MappedConfig
            {
                Model = "opencode/gpt-5-nano",
                Agent = "build",
            },
            cwd: null);

        Assert.DoesNotContain("--model", args);
        Assert.DoesNotContain("--agent", args);
    }

    [Fact]
    public void GetConfigFields_HasExpectedKeys()
    {
        var keys = OpenCodeCliConfigMapper.GetConfigFields().Select(f => f.Key).ToHashSet();
        Assert.Contains("agent", keys);
        Assert.Contains("dangerouslySkipPermissions", keys);
    }
}
