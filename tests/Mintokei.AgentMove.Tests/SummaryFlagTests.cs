using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentMove;

using Xunit;

namespace Mintokei.AgentMove.Tests;

/// <summary>
/// The summary flags, and the one that bites: <c>--summarise</c> takes an optional value, so it has
/// to decide whether the next token belongs to it. Getting that wrong swallows the flag after it.
/// </summary>
public class SummaryFlagTests
{
    private static SummarySettings Resolve(params string[] args) =>
        MoveOptions.Parse(args).ApplyTo(new SummarySettings());

    [Fact]
    public void Bare_summarise_means_always()
    {
        Assert.Equal(SummaryWhen.Always, Resolve("--summarise").When.When);
    }

    [Fact]
    public void A_count_after_summarise_is_the_threshold()
    {
        Assert.Equal(SummaryTrigger.Over(400), Resolve("--summarise", "400").When);
    }

    [Theory]
    [InlineData("--yes")]
    [InlineData("--attach")]
    [InlineData("--no-handoff")]
    public void Summarise_does_not_swallow_the_flag_after_it(string next)
    {
        // `agentmove --summarise --yes` must still be --yes. Consuming it would either fail on a
        // word that is not a trigger, or silently drop a flag the user typed.
        var options = MoveOptions.Parse(["--summarise", next]);

        Assert.Equal(SummaryWhen.Always, options.SummaryWhen!.Value.When);
        Assert.True(next switch
        {
            "--yes" => options.Yes,
            "--attach" => options.Attach,
            _ => options.NoHandoff,
        });
    }

    [Fact]
    public void No_summarise_wins_over_a_config_that_wanted_one()
    {
        var configured = new SummarySettings { When = SummaryTrigger.Always, With = "claude-fast" };

        var resolved = MoveOptions.Parse(["--no-summarise"]).ApplyTo(configured);

        Assert.Equal(SummaryWhen.Never, resolved.When.When);
        Assert.Equal("claude-fast", resolved.With);   // only the trigger was overridden
    }

    [Fact]
    public void A_flag_beats_the_config_file_and_silence_leaves_it_alone()
    {
        // Same rule as --handoff, deliberately: one precedence to learn, not two.
        var configured = new SummarySettings { When = SummaryTrigger.Over(400), With = "claude-fast" };

        Assert.Equal(SummaryTrigger.Always, MoveOptions.Parse(["--summarise"]).ApplyTo(configured).When);
        Assert.Equal("codex", MoveOptions.Parse(["--summarise-with", "codex"]).ApplyTo(configured).With);
        Assert.Equal(configured, MoveOptions.Parse([]).ApplyTo(configured));
    }

    [Theory]
    [InlineData("--summarize")]
    [InlineData("--summarise")]
    public void Both_spellings_are_accepted(string flag)
    {
        Assert.Equal(SummaryWhen.Always, Resolve(flag).When.When);
    }

    // ── who is allowed to write it ───────────────────────────────────────

    [Theory]
    // Claude asks in both of these, and the engine answers no.
    [InlineData(AgentToolKey.ClaudeCodeCli, "permissionMode", "default", true)]
    [InlineData(AgentToolKey.ClaudeCodeCli, "permissionMode", "plan", true)]
    [InlineData(AgentToolKey.ClaudeCodeCli, "permissionMode", "acceptEdits", false)]
    [InlineData(AgentToolKey.ClaudeCodeCli, "permissionMode", "bypassPermissions", false)]
    [InlineData(AgentToolKey.CodexCli, "sandbox", "read-only", true)]
    [InlineData(AgentToolKey.CodexCli, "sandbox", "workspace-write", false)]
    [InlineData(AgentToolKey.CodexCli, "sandbox", "danger-full-access", false)]
    [InlineData(AgentToolKey.GithubCopilotCli, "mode", "interactive", true)]
    [InlineData(AgentToolKey.GithubCopilotCli, "mode", "autopilot", false)]
    public void A_summariser_is_only_quiet_when_the_profile_says_so(
        AgentToolKey tool, string key, string value, bool expected)
    {
        var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [key] = value };

        Assert.Equal(expected, Backends.IsReadOnly(tool, config));
    }

    [Theory]
    [InlineData(AgentToolKey.ClaudeCodeCli)]
    [InlineData(AgentToolKey.CodexCli)]
    [InlineData(AgentToolKey.GithubCopilotCli)]
    [InlineData(AgentToolKey.OpenCodeCli)]
    public void Saying_nothing_is_not_saying_read_only(AgentToolKey tool)
    {
        // An empty profile means the CLI's own default, which differs per tool and per version.
        // Inferring "probably safe" from silence is how an agent gains reach nobody granted.
        Assert.False(Backends.IsReadOnly(tool, new Dictionary<string, string?>()));
    }

    [Fact]
    public void A_dangerous_override_disqualifies_an_otherwise_quiet_profile()
    {
        var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["permissionMode"] = "default",
            ["allowDangerouslySkipPermissions"] = "true",
        };

        Assert.False(Backends.IsReadOnly(AgentToolKey.ClaudeCodeCli, config));
    }
}
