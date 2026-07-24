using Microsoft.Extensions.Options;
using Mintokei.Sandbox;

namespace BrokerSandboxMinimal;

/// <summary>
/// Example <see cref="ISandboxBrokerSecretsProvider"/> — the product's side of broker egress: map a session to
/// the secrets the per-session broker injects, built with the library's convention helpers so no header string
/// is hand-rolled. A real product looks creds up per-tenant from its own store, keyed off
/// <see cref="SandboxSessionRequest.Name"/>; this demo just reads them from <see cref="BrokerDemoOptions"/>.
///
/// Registered with <c>AddMintokeiSandboxBrokerSecrets&lt;DemoBrokerSecrets&gt;()</c>; the runtime calls it at
/// provision time whenever a profile selects broker egress (both the pool and remote paths).
/// </summary>
public sealed class DemoBrokerSecrets(IOptions<BrokerDemoOptions> options) : ISandboxBrokerSecretsProvider
{
    public Task<SandboxBrokerSecrets?> ResolveAsync(
        SandboxSessionRequest request, SandboxProfile profile, CancellationToken ct = default)
    {
        var o = options.Value;
        var secrets = new SandboxBrokerSecrets();

        // Model: a raw OAuth token in → the correct injected header out (bearer + the oauth beta flag).
        if (!string.IsNullOrWhiteSpace(o.AnthropicOAuthToken))
            secrets = secrets.WithModel(ModelUpstreamSpec.AnthropicOAuth(o.AnthropicOAuthToken));

        // Git: already a "host=user:token" line here; build one from parts with SandboxBrokerSecrets.GitCredentialLine.
        if (!string.IsNullOrWhiteSpace(o.GitCredentials))
            secrets = secrets.WithGitCredentials(o.GitCredentials);

        if (!string.IsNullOrWhiteSpace(o.GitHubToken))
            secrets = secrets.WithGitHubToken(o.GitHubToken);

        return Task.FromResult<SandboxBrokerSecrets?>(secrets);
    }
}
