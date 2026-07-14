using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Tests for CopilotCliConfigMapper — the translation layer between mintokei's
/// key/value config dict and Copilot's CLI flags / launch-time options. Pure
/// functions; verifies that the always-present base flags are there, each
/// supported key maps to the right flag, and truthy/empty edge cases behave.
/// </summary>
public class CopilotCliConfigMapperTests
{
    // ── Map: raw dict → MappedConfig ──

    [Fact]
    public void Map_ExtractsStringFields()
    {
        var mapped = CopilotCliConfigMapper.Map(new Dictionary<string, string?>
        {
            ["model"] = "gpt-5-mini",
            ["effort"] = "high",
            ["mode"] = "autopilot",
        });

        Assert.Equal("gpt-5-mini", mapped.Model);
        Assert.Equal("high", mapped.Effort);
        Assert.Equal("autopilot", mapped.Mode);
    }

    [Fact]
    public void Map_Parses_MaxAutopilotContinues_AsInt()
    {
        var mapped = CopilotCliConfigMapper.Map(new Dictionary<string, string?>
        {
            ["maxAutopilotContinues"] = "42",
        });

        Assert.Equal(42, mapped.MaxAutopilotContinues);
    }

    [Fact]
    public void Map_MaxAutopilotContinues_Invalid_LeftNull()
    {
        var mapped = CopilotCliConfigMapper.Map(new Dictionary<string, string?>
        {
            ["maxAutopilotContinues"] = "not-a-number",
        });

        Assert.Null(mapped.MaxAutopilotContinues);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Map_BoolFlags_ParseTruthy(string? value, bool expected)
    {
        var mapped = CopilotCliConfigMapper.Map(new Dictionary<string, string?>
        {
            ["disableAskUser"] = value,
        });

        Assert.Equal(expected, mapped.DisableAskUser);
    }

    [Fact]
    public void Map_UnknownKeys_AreIgnored()
    {
        var mapped = CopilotCliConfigMapper.Map(new Dictionary<string, string?>
        {
            ["unknownKey"] = "whatever",
            ["anotherUnknown"] = "yes",
        });

        Assert.Null(mapped.Model);
        Assert.Null(mapped.Effort);
        Assert.False(mapped.DisableAskUser);
    }

    [Fact]
    public void Map_EmptyDict_ReturnsDefaults()
    {
        var mapped = CopilotCliConfigMapper.Map(new Dictionary<string, string?>());

        Assert.Null(mapped.Model);
        Assert.Null(mapped.Effort);
        Assert.Null(mapped.Mode);
        Assert.False(mapped.DisableAskUser);
        Assert.False(mapped.DisableBuiltinMcps);
        Assert.False(mapped.EnableAllGithubMcpTools);
        Assert.False(mapped.AllowAllPaths);
        Assert.Null(mapped.MaxAutopilotContinues);
    }

    // ── ToCliArguments: MappedConfig → CLI flags ──

    [Fact]
    public void ToCliArguments_DefaultConfig_ProducesBaselineFlags()
    {
        var args = CopilotCliConfigMapper.ToCliArguments(new CopilotCliConfigMapper.MappedConfig());

        // These two are always present — ACP mode + no self-update.
        Assert.True(args.ContainsKey("--acp"));
        Assert.True(args.ContainsKey("--no-auto-update"));

        // No optional flags for default config.
        Assert.False(args.ContainsKey("--model"));
        Assert.False(args.ContainsKey("--effort"));
        Assert.False(args.ContainsKey("--mode"));
        Assert.False(args.ContainsKey("--no-ask-user"));
    }

    [Fact]
    public void ToCliArguments_MapsModelAndEffort()
    {
        var args = CopilotCliConfigMapper.ToCliArguments(new CopilotCliConfigMapper.MappedConfig
        {
            Model = "claude-sonnet-4.6",
            Effort = "xhigh",
            Mode = "plan",
        });

        Assert.Equal("claude-sonnet-4.6", args["--model"]);
        Assert.Equal("xhigh", args["--effort"]);
        Assert.Equal("plan", args["--mode"]);
    }

    [Fact]
    public void ToCliArguments_BoolFlags_PresentOnlyWhenTrue()
    {
        var args = CopilotCliConfigMapper.ToCliArguments(new CopilotCliConfigMapper.MappedConfig
        {
            DisableAskUser = true,
            DisableBuiltinMcps = true,
            EnableAllGithubMcpTools = false,
            AllowAllPaths = true,
        });

        Assert.True(args.ContainsKey("--no-ask-user"));
        Assert.True(args.ContainsKey("--disable-builtin-mcps"));
        Assert.True(args.ContainsKey("--allow-all-paths"));
        Assert.False(args.ContainsKey("--enable-all-github-mcp-tools"));
    }

    [Fact]
    public void ToCliArguments_MaxAutopilotContinues_StringifiesInt()
    {
        var args = CopilotCliConfigMapper.ToCliArguments(new CopilotCliConfigMapper.MappedConfig
        {
            MaxAutopilotContinues = 10,
        });

        Assert.Equal("10", args["--max-autopilot-continues"]);
    }

    [Fact]
    public void ToCliArguments_EmptyStringModel_OmitsFlag()
    {
        // A null-or-whitespace model from the raw config should not produce a
        // `--model ""` arg — Copilot would error. Guard rails here.
        var args = CopilotCliConfigMapper.ToCliArguments(new CopilotCliConfigMapper.MappedConfig
        {
            Model = "",
        });

        Assert.False(args.ContainsKey("--model"));
    }

    // ── GetConfigFields (UI descriptors) ──

    [Fact]
    public void GetConfigFields_HasAllExpectedKeys()
    {
        var fields = CopilotCliConfigMapper.GetConfigFields();
        var keys = fields.Select(f => f.Key).ToHashSet();

        // Keys the webapp config panel expects to see for the Copilot tool.
        Assert.Contains("effort", keys);
        Assert.Contains("mode", keys);
        Assert.Contains("disableAskUser", keys);
        Assert.Contains("disableBuiltinMcps", keys);
        Assert.Contains("enableAllGithubMcpTools", keys);
        Assert.Contains("allowAllPaths", keys);
        Assert.Contains("maxAutopilotContinues", keys);
    }
}
