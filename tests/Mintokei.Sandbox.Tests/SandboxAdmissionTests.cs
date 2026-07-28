using Mintokei.Sandbox;
using Mintokei.Sandbox.Docker;
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

/// <summary>
/// The declaration is carried ON the sandbox (a Docker label / pod annotation), so admission checks what the
/// sandbox was BUILT with rather than what a caller believes — surviving an API restart, a DB rollback, or a
/// second embedder of the library.
/// </summary>
public class SandboxAdmissionLabelTests
{
    [Fact]
    public void Round_trips_a_declaration_through_the_label_value()
    {
        var parsed = SandboxAdmission.ParseAdmittedTools("ClaudeCodeCli,CodexCli");
        Assert.Equal(["ClaudeCodeCli", "CodexCli"], parsed);
        Assert.True(SandboxAdmission.Admits(parsed, "CodexCli"));
        Assert.False(SandboxAdmission.Admits(parsed, "GithubCopilotCli"));
    }

    [Fact]
    public void Tolerates_whitespace_and_empty_entries()
    {
        Assert.Equal(["ClaudeCodeCli", "CodexCli"],
            SandboxAdmission.ParseAdmittedTools(" ClaudeCodeCli , , CodexCli "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_declaration_reads_as_unconstrained_not_as_admits_nothing(string? value)
    {
        // This is the ONE place the default runs toward permissive, and deliberately: a sandbox provisioned
        // before this shipped genuinely has no declaration, and "admits nothing" would strand every sandbox
        // running at deploy time. Safe only because sharing REQUIRES a declaration to check against — an
        // undeclared sandbox is never shared, so nothing can slip in beside an existing session.
        var parsed = SandboxAdmission.ParseAdmittedTools(value);
        Assert.Empty(parsed);
        Assert.True(SandboxAdmission.Admits(parsed, "anything"));
    }
}

/// <summary>
/// The declaration has to survive the whole chain — session request → spec → the label actually written onto
/// the container. A break anywhere leaves AdmittedTools set on paper while every running sandbox reads back as
/// unconstrained, which fails OPEN: sharing would then admit anything.
/// </summary>
public class SandboxAdmissionWiringTests
{
    [Fact]
    public void The_declaration_survives_request_to_spec()
    {
        var factory = new SandboxSpecFactory(Microsoft.Extensions.Options.Options.Create(new SandboxOptions()));
        var profile = new SandboxProfileResolver(
            Microsoft.Extensions.Options.Options.Create(new SandboxOptions())).Resolve();

        var spec = factory.Build(profile, new SandboxSessionRequest
        {
            Name = "sb-1",
            BackendUrl = "http://backend",
            EnrollmentToken = "tok",
            AdmittedTools = ["ClaudeCodeCli"],
        });

        Assert.Equal(["ClaudeCodeCli"], spec.AdmittedTools);
    }

    [Fact]
    public void The_declaration_reaches_the_container_label()
    {
        var spec = new SandboxSpec
        {
            Image = "img",
            Name = "sb-1",
            RuntimeClass = "runc",
            Limits = new SandboxResourceLimits(1024, 1, 64),
            AdmittedTools = ["ClaudeCodeCli", "CodexCli"],
        };

        var args = string.Join(' ', DockerCommand.BuildRunArgs(spec));

        Assert.Contains($"{DockerCommand.AdmittedToolsLabel}=ClaudeCodeCli,CodexCli", args);
        // And it round-trips through the parser the attach path will use.
        Assert.True(SandboxAdmission.Admits(
            SandboxAdmission.ParseAdmittedTools("ClaudeCodeCli,CodexCli"), "CodexCli"));
    }

    [Fact]
    public void An_undeclared_sandbox_writes_no_label_at_all()
    {
        var spec = new SandboxSpec
        {
            Image = "img",
            Name = "sb-1",
            RuntimeClass = "runc",
            Limits = new SandboxResourceLimits(1024, 1, 64),
        };

        var args = string.Join(' ', DockerCommand.BuildRunArgs(spec));
        Assert.DoesNotContain(DockerCommand.AdmittedToolsLabel, args);
    }
}
