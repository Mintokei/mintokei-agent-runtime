using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentMove;

using Xunit;

namespace Mintokei.AgentMove.Tests;

public class CliArgsTests
{
    private static Profile Codex(params (string Key, string Value)[] config) => Profile("codex", config);
    private static Profile Claude(params (string Key, string Value)[] config) => Profile("claude", config);

    private static Profile Profile(string tool, (string Key, string Value)[] config)
    {
        var p = new Profile { Tool = tool };
        foreach (var (key, value) in config)
            p.Config[key] = value;
        return p;
    }

    [Fact]
    public void Codex_permission_settings_become_its_own_flags()
    {
        var (args, dropped) = CliArgs.For(
            AgentToolKey.CodexCli, Codex(("sandbox", "read-only"), ("approvalPolicy", "on-request")));

        Assert.Equal(["--ask-for-approval", "on-request", "--sandbox", "read-only"], args);
        Assert.Empty(dropped);
    }

    [Fact]
    public void Codex_config_only_settings_go_through_a_config_override()
    {
        // These have no flag of their own; -c is the form Codex accepts, and the field names were
        // checked against `codex exec --strict-config`, which rejects an unknown one.
        var (args, _) = CliArgs.For(AgentToolKey.CodexCli, Codex(("effort", "low")));

        Assert.Equal(["--config", "model_reasoning_effort=low"], args);
    }

    [Fact]
    public void NoProjectDoc_is_a_config_override_because_the_flag_does_not_exist()
    {
        // `--no-project-doc` is rejected by the CLI outright, which took the whole session down
        // when the engine passed it. Zeroing the budget is what actually suppresses AGENTS.md.
        var (args, _) = CliArgs.For(AgentToolKey.CodexCli, Codex(("noProjectDoc", "true")));

        Assert.DoesNotContain("--no-project-doc", args);
        Assert.Equal(["--config", "project_doc_max_bytes=0"], args);
    }

    [Fact]
    public void A_false_boolean_produces_no_flag()
    {
        var (args, _) = CliArgs.For(AgentToolKey.CodexCli, Codex(("webSearch", "false")));
        Assert.Empty(args);
    }

    [Fact]
    public void Claude_reuses_the_engines_own_mapper()
    {
        var (args, dropped) = CliArgs.For(
            AgentToolKey.ClaudeCodeCli, Claude(("model", "claude-sonnet-4-5"), ("permissionMode", "acceptEdits")));

        Assert.Contains("--permission-mode", args);
        Assert.Contains("acceptEdits", args);
        Assert.Contains("--model", args);
        Assert.Empty(dropped);
    }

    [Fact]
    public void Copilot_gets_its_interface_flags_but_not_the_engines_acp_launch()
    {
        // The engine's mapper is written for its own ACP launch and always emits --acp, which
        // would start the protocol server instead of the interface a person wants to look at.
        var (args, _) = CliArgs.For(
            AgentToolKey.GithubCopilotCli, Profile("copilot", [("mode", "interactive"), ("model", "gpt-5.5")]));

        Assert.DoesNotContain("--acp", args);
        Assert.Contains("--mode", args);
        Assert.Contains("interactive", args);
    }

    // ── the invocation ───────────────────────────────────────────────────

    [Fact]
    public void Codex_resumes_by_subcommand_and_the_others_by_flag()
    {
        var codex = Reporting.Resume(AgentToolKey.CodexCli, "abc", Codex());
        Assert.Equal("codex", codex.File);
        Assert.Equal(["resume", "abc"], codex.Argv);

        var claude = Reporting.Resume(AgentToolKey.ClaudeCodeCli, "abc", Claude());
        Assert.Equal(["--resume", "abc"], claude.Argv);
    }

    [Fact]
    public void The_opening_turn_comes_last_so_it_is_not_read_as_a_flags_value()
    {
        var (_, argv, _) = Reporting.Resume(
            AgentToolKey.CodexCli, "abc", Codex(("sandbox", "read-only")), firstTurn: "carry on");

        Assert.Equal("carry on", argv[^1]);
    }

    [Fact]
    public void Copilot_needs_a_flag_for_the_opening_turn()
    {
        var (_, argv, _) = Reporting.Resume(
            AgentToolKey.GithubCopilotCli, "abc", Profile("copilot", []), firstTurn: "carry on");

        Assert.Equal(["--resume", "abc", "-i", "carry on"], argv);
    }

    [Fact]
    public void ExtraArgs_come_after_the_mapped_ones_and_before_the_turn()
    {
        var profile = Codex(("sandbox", "read-only"));
        profile.ExtraArgs.Add("--search");

        var (_, argv, _) = Reporting.Resume(AgentToolKey.CodexCli, "abc", profile, firstTurn: "go");

        Assert.Equal(["resume", "abc", "--sandbox", "read-only", "--search", "go"], argv);
    }

    [Fact]
    public void A_multiline_turn_survives_as_one_argument()
    {
        // The handoff is multi-line by default; argv keeps it whole, a shell string would not.
        var (_, argv, _) = Reporting.Resume(
            AgentToolKey.CodexCli, "abc", Codex(), firstTurn: "line one\nline two");

        Assert.Equal("line one\nline two", Assert.Single(argv, a => a.Contains('\n')));
    }
}
