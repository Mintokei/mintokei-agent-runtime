using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>The default provider: it injects ONLY the providers a session declares (least privilege), reads the
/// creds from the configured locations, and never fails a launch over a missing/unknown credential.</summary>
public sealed class HostCredentialsBrokerSecretsProviderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mk-broker-cred").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    private void Write(string file, string content) => File.WriteAllText(Path.Combine(_dir, file), content);

    private HostCredentialsBrokerSecretsProvider Provider() => new(
        Options.Create(new SandboxOptions
        {
            BrokerCredentials = new SandboxBrokerCredentialLocations { AnthropicDir = _dir, OpenAiDir = _dir, GitDir = _dir },
        }),
        NullLogger<HostCredentialsBrokerSecretsProvider>.Instance);

    private static SandboxSessionRequest Request(SandboxBrokerNeeds? needs) =>
        new() { BackendUrl = "https://api", EnrollmentToken = "t", Name = "s1", Broker = needs };

    private static SandboxProfile Profile() =>
        new("broker", "runc", new SandboxResourceLimits(1, 1, 1), SandboxEgress.Broker, null);

    [Fact]
    public async Task Injects_only_anthropic_for_a_claude_session_even_when_an_openai_key_is_present()
    {
        Write(".credentials.json", """{"claudeAiOauth":{"accessToken":"sk-ant-oat-T"}}""");
        Write("auth.json", """{"OPENAI_API_KEY":"sk-openai"}""");    // present but NOT needed → must be ignored

        var s = await Provider().ResolveAsync(Request(new(["anthropic"], Git: false)), Profile());

        var m = Assert.Single(s!.EffectiveModelUpstreams);
        Assert.Equal("anthropic", m.Provider);
        Assert.Contains("sk-ant-oat-T", m.Auth);
        Assert.Null(s.GitCredentials);
        Assert.Null(s.GitHubToken);
    }

    [Fact]
    public async Task Injects_only_openai_for_a_codex_session()
    {
        Write("auth.json", """{"OPENAI_API_KEY":"sk-openai-99"}""");

        var s = await Provider().ResolveAsync(Request(new(["openai"])), Profile());

        var m = Assert.Single(s!.EffectiveModelUpstreams);
        Assert.Equal("openai", m.Provider);
        Assert.Contains("sk-openai-99", m.Auth);
    }

    [Fact]
    public async Task Adds_git_credentials_only_when_the_session_asks_for_them()
    {
        Write(".git-credentials", "https://x:ght@github.com\n");

        var with = await Provider().ResolveAsync(Request(new([], Git: true)), Profile());
        Assert.Equal("github.com=x:ght", with!.GitCredentials);

        var without = await Provider().ResolveAsync(Request(new([], Git: false)), Profile());
        Assert.Null(without!.GitCredentials);
    }

    [Fact]
    public async Task Null_needs_injects_nothing()
    {
        var s = await Provider().ResolveAsync(Request(null), Profile());
        Assert.Empty(s!.EffectiveModelUpstreams);
        Assert.Null(s.GitCredentials);
    }

    [Fact]
    public async Task Unknown_or_uncredentialed_provider_is_skipped_not_fatal()
    {
        // no .credentials.json written → anthropic has no readable cred; "bogus" is unknown. Neither throws.
        var s = await Provider().ResolveAsync(Request(new(["anthropic", "bogus"])), Profile());
        Assert.Empty(s!.EffectiveModelUpstreams);
    }
}
