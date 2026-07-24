using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>The file-REFERENCE provider (K8s broker): it emits ${json:…}/${gitcreds:…} refs + the cred dirs to
/// mount, NEVER reading the tokens — so the token stays on the node and is resolved broker-side. Sources the same
/// configured locations as the reading provider, and injects only what a session declares.</summary>
public sealed class HostCredentialsFileRefBrokerSecretsProviderTests
{
    private static HostCredentialsFileRefBrokerSecretsProvider Provider() => new(
        Options.Create(new SandboxOptions
        {
            BrokerCredentials = new SandboxBrokerCredentialLocations
            {
                AnthropicDir = "/root/.claude", OpenAiDir = "/root/.codex", GitDir = "/root/sandbox-git-creds",
            },
        }),
        NullLogger<HostCredentialsFileRefBrokerSecretsProvider>.Instance);

    private static SandboxSessionRequest Request(SandboxBrokerNeeds? needs) =>
        new() { BackendUrl = "https://api", EnrollmentToken = "t", Name = "s1", Broker = needs };

    private static SandboxProfile Profile() =>
        new("broker", "runc", new SandboxResourceLimits(1, 1, 1), SandboxEgress.Broker, null);

    [Fact]
    public async Task Emits_a_json_ref_and_a_mount_for_the_declared_provider_without_reading_a_token()
    {
        var s = await Provider().ResolveAsync(Request(new(["anthropic"], Git: false)), Profile());

        // The auth header is a REFERENCE (path), not a token — resolved broker-side from the staged mount.
        var m = Assert.Single(s!.EffectiveModelUpstreams);
        Assert.Equal("anthropic", m.Provider);
        Assert.Contains("${json:/creds/claude/.credentials.json#claudeAiOauth.accessToken}", m.Auth);
        // The node cred dir is declared as a mount the broker Pod stages.
        var mount = Assert.Single(s.CredentialMounts);
        Assert.Equal("/root/.claude", mount.HostDir);
        Assert.Equal("/creds/claude", mount.ContainerDir);
    }

    [Fact]
    public async Task Git_needs_add_a_gitcreds_ref_and_the_git_dir_mount()
    {
        var s = await Provider().ResolveAsync(Request(new(["anthropic"], Git: true)), Profile());

        Assert.Equal("${gitcreds:/creds/git/.git-credentials}", s!.GitCredentials);
        Assert.Contains(s.CredentialMounts, x => x.HostDir == "/root/sandbox-git-creds" && x.ContainerDir == "/creds/git");
    }

    [Fact]
    public async Task Injects_only_the_declared_provider_least_privilege()
    {
        // openai cred dir is configured, but a claude-only session must not get an openai upstream or mount.
        var s = await Provider().ResolveAsync(Request(new(["anthropic"], Git: false)), Profile());
        Assert.DoesNotContain(s!.EffectiveModelUpstreams, u => u.Provider == "openai");
        Assert.DoesNotContain(s.CredentialMounts, x => x.ContainerDir == "/creds/codex");
    }

    [Fact]
    public async Task No_needs_injects_nothing()
    {
        var s = await Provider().ResolveAsync(Request(null), Profile());
        Assert.Empty(s!.EffectiveModelUpstreams);
        Assert.Empty(s.CredentialMounts);
        Assert.Null(s.GitCredentials);
    }
}
