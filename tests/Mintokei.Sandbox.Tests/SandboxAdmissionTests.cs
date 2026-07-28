using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// The rule guarding a SHARED sandbox. Sharing a sandbox shares its broker, and a broker cannot tell which
/// session a connection belongs to — so its allowlist and injected credentials apply to everyone inside.
/// Every test here is really asserting the same thing: a session must never be admitted into a sandbox whose
/// reach it would widen for the sessions already there.
/// </summary>
public class SandboxAdmissionTests
{
    [Fact]
    public void An_unconstrained_sandbox_admits_anything()
    {
        // Empty declaration = the single-session case: the sandbox serves exactly the session it was
        // provisioned for, so there is nothing to admit and nothing to widen.
        Assert.True(SandboxAdmission.Admits([], "ClaudeCodeCli"));
        SandboxAdmission.EnsureAdmits("sb", [], "ClaudeCodeCli");
    }

    [Fact]
    public void A_declared_sandbox_admits_its_own_tool()
    {
        Assert.True(SandboxAdmission.Admits(["ClaudeCodeCli"], "ClaudeCodeCli"));
        Assert.True(SandboxAdmission.Admits(["ClaudeCodeCli", "CodexCli"], "CodexCli"));
    }

    [Fact]
    public void A_declared_sandbox_refuses_a_tool_it_was_not_built_for()
    {
        Assert.False(SandboxAdmission.Admits(["ClaudeCodeCli"], "CodexCli"));

        var ex = Assert.Throws<SandboxAdmissionException>(
            () => SandboxAdmission.EnsureAdmits("sb-1", ["ClaudeCodeCli"], "CodexCli"));

        // The message has to carry all three facts — which sandbox, what it serves, what was asked — because
        // the fix depends on all of them.
        Assert.Contains("sb-1", ex.Message);
        Assert.Contains("ClaudeCodeCli", ex.Message);
        Assert.Contains("CodexCli", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_tool_never_passes_a_constrained_sandbox(string? requested)
    {
        // Fail closed. A missing tool is a caller bug, and the dangerous reading of "unknown" is "allow".
        Assert.False(SandboxAdmission.Admits(["ClaudeCodeCli"], requested!));
        Assert.Throws<SandboxAdmissionException>(
            () => SandboxAdmission.EnsureAdmits("sb", ["ClaudeCodeCli"], requested!));
    }

    [Fact]
    public void Tool_matching_ignores_case()
    {
        // Tool keys reach this from config, a DB column and an enum name; a casing difference must not read
        // as "different tool" and provision a redundant sandbox.
        Assert.True(SandboxAdmission.Admits(["claudecodecli"], "ClaudeCodeCli"));
        Assert.True(SandboxAdmission.Admits(["ClaudeCodeCli"], "CLAUDECODECLI"));
    }

    [Fact]
    public void Refusal_is_the_default_for_anything_not_explicitly_listed()
    {
        // The property that matters, stated directly: for a constrained sandbox, membership is the ONLY way in.
        string[] declared = ["ClaudeCodeCli"];
        foreach (var other in new[] { "CodexCli", "GithubCopilotCli", "OpenCode", "something-new" })
            Assert.False(SandboxAdmission.Admits(declared, other), $"{other} must not be admitted");
    }
}
