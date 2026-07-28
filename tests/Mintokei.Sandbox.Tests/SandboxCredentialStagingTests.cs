using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// Guards the shared staging policy. The bug this replaces was not a wrong rule — it was the SAME rule
/// existing in three places and only one of them learning it: the remote worker path trimmed the agent home,
/// while both Kubernetes init containers kept copying it wholesale into a tmpfs emptyDir (~1.1GB of memory and
/// ~35s of pod startup, per session, for a few KB of credentials). Nothing errored, so nothing caught it.
/// </summary>
public class SandboxCredentialStagingTests
{
    [Theory]
    // The CLI's own dot-names (seed mounts) and the bare names under /creds (broker mounts) are the same
    // source, so they must classify identically — the split that let the paths drift.
    [InlineData(".claude", SandboxCredentialKind.ClaudeHome)]
    [InlineData("/seed/.claude", SandboxCredentialKind.ClaudeHome)]
    [InlineData("/creds/claude", SandboxCredentialKind.ClaudeHome)]
    [InlineData(".codex", SandboxCredentialKind.CodexHome)]
    [InlineData("/creds/codex", SandboxCredentialKind.CodexHome)]
    [InlineData(".claude.json", SandboxCredentialKind.ClaudeJson)]
    [InlineData("/creds/git", SandboxCredentialKind.GitCreds)]
    [InlineData("git", SandboxCredentialKind.GitCreds)]
    [InlineData("something-else", SandboxCredentialKind.Unknown)]
    public void Classifies_both_spellings_of_every_source(string path, SandboxCredentialKind expected)
        => Assert.Equal(expected, SandboxCredentialStaging.KindFor(path));

    [Fact]
    public void Agent_home_keeps_config_but_drops_the_caches_and_history()
    {
        var cmd = SandboxCredentialStaging.CopyCommand(
            "/stage-in/.claude", "/stage-out/.claude",
            SandboxCredentialKind.ClaudeHome, SandboxStagingScope.AgentHome);

        Assert.StartsWith("cptrim ", cmd);
        // The GB-scale subtrees. plugins is the one that blew the staging timeout on a real runner.
        Assert.Contains("plugins", cmd);
        Assert.Contains("projects", cmd);
        Assert.Contains("history.jsonl", cmd);
    }

    [Fact]
    public void Broker_takes_only_the_files_its_refs_read()
    {
        var cmd = SandboxCredentialStaging.CopyCommand(
            "/stage-in/0", "/stage-out/0",
            SandboxCredentialKind.ClaudeHome, SandboxStagingScope.BrokerSecrets);

        // Allow-list, not exclude-list: the broker runs no CLI, so anything that later appears in the agent
        // home is excluded by default rather than by someone remembering to add it here.
        Assert.StartsWith("cpick ", cmd);
        Assert.Contains(".credentials.json", cmd);
        Assert.DoesNotContain("cptrim", cmd);
        // Conversation transcripts must never be staged into a broker that cannot read them anyway.
        Assert.DoesNotContain("projects", cmd);
    }

    [Fact]
    public void Broker_scope_is_strictly_narrower_than_agent_home_for_every_kind()
    {
        // The property that matters, stated once instead of per-kind: a broker never receives more than the
        // agent does. If a future kind is added and wired only into AgentHome, this fails.
        foreach (var kind in Enum.GetValues<SandboxCredentialKind>())
        {
            var agent = SandboxCredentialStaging.CopyCommand("/in", "/out", kind, SandboxStagingScope.AgentHome);
            var broker = SandboxCredentialStaging.CopyCommand("/in", "/out", kind, SandboxStagingScope.BrokerSecrets);

            if (kind is SandboxCredentialKind.ClaudeHome or SandboxCredentialKind.CodexHome)
                Assert.True(broker.StartsWith("cpick ") && agent.StartsWith("cptrim "),
                    $"{kind}: broker must allow-list where the agent trims");
            else
                Assert.Equal(agent, broker); // single files / small dirs: identical either way
        }
    }

    [Fact]
    public void Unknown_sources_are_copied_whole_rather_than_guessed_at()
    {
        // Trimming something we don't understand could silently drop a credential, which fails at run time as
        // an auth error far from here. Copying too much is recoverable; copying too little is not.
        var cmd = SandboxCredentialStaging.CopyCommand(
            "/in", "/out", SandboxCredentialKind.Unknown, SandboxStagingScope.AgentHome);
        Assert.Equal("cptrim /in /out", cmd);
    }

    [Fact]
    public void Shell_helpers_tolerate_a_missing_source_without_failing_the_stage()
    {
        // Callers run under `set -e`; an absent optional credential must not abort provisioning.
        Assert.Contains("cptrim()", SandboxCredentialStaging.ShellFunctions);
        Assert.Contains("cpick()", SandboxCredentialStaging.ShellFunctions);
        Assert.Contains("|| true", SandboxCredentialStaging.ShellFunctions);
        Assert.Contains("tar -chf", SandboxCredentialStaging.ShellFunctions); // -h dereferences symlinks
    }
}
