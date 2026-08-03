using System.Text.Json;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.Codex;
using Mintokei.AgentEngine.Copilot;
using Mintokei.AgentEngine.OpenCode;
using Mintokei.AgentMove;

using Xunit;

namespace Mintokei.AgentMove.Tests;

/// <summary>
/// <see cref="Backends"/> mirrors, by hand, which config keys each engine mapper consumes. Every
/// bug this tool has had came from that copy drifting: <c>access</c> where Codex means
/// <c>sandbox</c>, <c>autopilot</c> where Copilot means <c>mode</c> — both accepted, mapped to
/// nothing, and reported to the user as if they were in force.
///
/// So the table is not trusted here. Each claim in it is put to the mapper it claims to describe.
/// </summary>
public class BackendTableTests
{
    public static TheoryData<AgentToolKey> Tools =>
    [
        AgentToolKey.ClaudeCodeCli,
        AgentToolKey.CodexCli,
        AgentToolKey.GithubCopilotCli,
        AgentToolKey.OpenCodeCli,
    ];

    [Theory]
    [MemberData(nameof(Tools))]
    public void Every_accepted_key_actually_changes_what_the_mapper_produces(AgentToolKey tool)
    {
        // The direct check for the bug that keeps happening: a key the mapper's switch has no case
        // for is silently discarded, so mapping with it must differ from mapping without it.
        var baseline = Map(tool, []);

        foreach (var key in Backends.AcceptedKeys(tool))
        {
            var withKey = Map(tool, new Dictionary<string, string?> { [key] = ValueFor(key) });
            Assert.True(
                withKey != baseline,
                $"{tool} is documented as accepting '{key}', but its config mapper ignores it — "
                + $"a profile setting it would be reported as applied and never sent.");
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Every_key_the_engine_offers_is_either_accepted_or_refused_with_a_reason(AgentToolKey tool)
    {
        // The other direction: the engine gains a key and agentmove keeps quiet about it. Silence
        // is the wrong answer either way — accept it, or say why it cannot be delivered.
        foreach (var field in ConfigFields(tool))
        {
            var known = Backends.AcceptedKeys(tool).Contains(field)
                || Backends.Unsupported(field, out _);
            Assert.True(known,
                $"{tool} offers config key '{field}' and agentmove neither accepts nor refuses it. "
                + "Add it to Backends.AcceptedKeys, or to Unsupported with the reason.");
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void No_key_is_both_accepted_and_refused(AgentToolKey tool)
    {
        foreach (var key in Backends.AcceptedKeys(tool))
            Assert.False(Backends.Unsupported(key, out _), $"'{key}' is both accepted and refused for {tool}.");
    }

    [Fact]
    public void Refusals_carry_a_reason()
    {
        foreach (var key in new[] { "ephemeral", "collaborationMode" })
        {
            Assert.True(Backends.Unsupported(key, out var why));
            Assert.False(string.IsNullOrWhiteSpace(why), $"'{key}' is refused without saying why.");
        }
    }

    [Theory]
    [InlineData("access", "sandbox")]              // Codex's own name for it
    [InlineData("approvalMode", "approvalPolicy")]
    [InlineData("reasoningEffort", "effort")]
    public void A_wrong_name_suggests_the_right_one(string wrong, string right)
    {
        var config = new Dictionary<string, string?> { [wrong] = "x" };
        var unknown = Backends.Unknown(AgentToolKey.CodexCli, config);

        var (key, suggestion) = Assert.Single(unknown);
        Assert.Equal(wrong, key);
        Assert.Equal(right, suggestion);
    }

    [Fact]
    public void A_permission_key_is_recognised_for_every_backend_that_has_one()
    {
        // Not a union check for its own sake: PermissionSettings() drives what agentmove prints
        // before it acts, and a permission key missing from it is one that goes unannounced.
        Assert.True(Backends.IsPermissionKey("permissionMode"));   // Claude
        Assert.True(Backends.IsPermissionKey("sandbox"));          // Codex
        Assert.True(Backends.IsPermissionKey("mode"));             // Copilot
        Assert.True(Backends.IsPermissionKey("dangerouslySkipPermissions"));  // OpenCode
        Assert.False(Backends.IsPermissionKey("model"));
    }

    // ── driving the real mappers ─────────────────────────────────────────

    /// <summary>
    /// A mapper's output as JSON, so "did this key change anything" is a value comparison rather
    /// than a reference one — none of the MappedConfig types implement equality.
    /// </summary>
    private static string Map(AgentToolKey tool, Dictionary<string, string?> config) => tool switch
    {
        AgentToolKey.ClaudeCodeCli => JsonSerializer.Serialize(ClaudeCodeConfigMapper.MapToCliArgs(config)),
        AgentToolKey.CodexCli => JsonSerializer.Serialize(CodexConfigMapper.Map(config)),
        AgentToolKey.GithubCopilotCli => JsonSerializer.Serialize(CopilotCliConfigMapper.Map(config)),
        AgentToolKey.OpenCodeCli => JsonSerializer.Serialize(OpenCodeCliConfigMapper.Map(config)),
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    private static IEnumerable<string> ConfigFields(AgentToolKey tool) => (tool switch
    {
        AgentToolKey.ClaudeCodeCli => ClaudeCodeConfigMapper.GetConfigFields(),
        AgentToolKey.CodexCli => CodexConfigMapper.GetConfigFields(),
        AgentToolKey.GithubCopilotCli => CopilotCliConfigMapper.GetConfigFields(),
        AgentToolKey.OpenCodeCli => OpenCodeCliConfigMapper.GetConfigFields(),
        _ => [],
    }).Select(f => f.Key);

    /// <summary>A value each key will accept — booleans must be truthy or the mapper skips them.</summary>
    private static string ValueFor(string key) => key.ToLowerInvariant() switch
    {
        "allowdangerouslyskippermissions" or "verbose" or "websearch" or "ephemeral"
            or "noprojectdoc" or "disableaskuser" or "disablebuiltinmcps"
            or "enableallgithubmcptools" or "allowallpaths" or "dangerouslyskippermissions" => "true",
        "maxturns" or "maxautopilotcontinues" => "5",
        _ => "x",
    };
}
