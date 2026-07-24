using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>The credential-convention builders — the reusable header/line formats live in the library so no
/// consumer re-derives them. These lock the exact wire shapes the broker + upstreams expect.</summary>
public class SandboxBrokerSecretsTests
{
    [Fact]
    public void AnthropicOAuth_builds_the_subscription_bearer_plus_oauth_beta()
    {
        var m = ModelUpstreamSpec.AnthropicOAuth("sk-ant-oat-XYZ");

        Assert.Equal("anthropic", m.Provider);
        Assert.Equal("https://api.anthropic.com", m.Upstream);
        Assert.Equal("Authorization: Bearer sk-ant-oat-XYZ;anthropic-beta: oauth-2025-04-20", m.Auth);
    }

    [Fact]
    public void OpenAiApiKey_builds_a_bearer_on_the_openai_provider()
    {
        var m = ModelUpstreamSpec.OpenAiApiKey("sk-openai-123");

        Assert.Equal("openai", m.Provider);
        Assert.Equal("https://api.openai.com", m.Upstream);
        Assert.Equal("Authorization: Bearer sk-openai-123", m.Auth);
    }

    [Fact]
    public void GitCredentialLine_uses_the_host_equals_user_colon_token_form()
        => Assert.Equal("github.com=x-access-token:ght_abc",
            SandboxBrokerSecrets.GitCredentialLine("github.com", "x-access-token", "ght_abc"));

    [Fact]
    public void Fluent_composition_sets_model_git_and_github()
    {
        var s = new SandboxBrokerSecrets()
            .WithModel(ModelUpstreamSpec.AnthropicOAuth("oat"))
            .WithGitCredentials("github.com=x:tok")
            .WithGitHubToken("github_pat_xyz");

        var m = Assert.Single(s.EffectiveModelUpstreams);
        Assert.Equal("anthropic", m.Provider);
        Assert.Equal("github.com=x:tok", s.GitCredentials);
        Assert.Equal("github_pat_xyz", s.GitHubToken);
    }

    [Fact]
    public void WithModel_folds_in_the_legacy_scalar_then_supersedes_it()
    {
        var s = new SandboxBrokerSecrets(ModelUpstream: "https://api.anthropic.com", ModelAuth: "x-api-key: k")
            .WithModel(ModelUpstreamSpec.OpenAiApiKey("sk-openai"));

        // The legacy anthropic scalar is folded into the explicit list alongside the new openai upstream…
        Assert.Collection(s.EffectiveModelUpstreams,
            a => Assert.Equal("anthropic", a.Provider),
            b => Assert.Equal("openai", b.Provider));
        // …and the scalar is cleared so it isn't also counted a second time.
        Assert.Null(s.ModelUpstream);
        Assert.Null(s.ModelAuth);
    }
}
