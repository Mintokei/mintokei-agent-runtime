using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mintokei.Sandbox;

/// <summary>
/// The out-of-the-box <see cref="ISandboxBrokerSecretsProvider"/>: assembles the MINIMAL broker secrets for a
/// session from (a) what it needs — <see cref="SandboxSessionRequest.Broker"/>, set by the product from the
/// session's tool — and (b) where the creds live — <see cref="SandboxOptions.BrokerCredentials"/>, from config.
/// Reads the standard cred files via <see cref="SandboxCredentialSources"/> and builds with the convention
/// helpers, so a host only supplies the two product-specific bits (which providers + where) and gets injection
/// for free. Register with <c>AddMintokeiHostCredentialsBrokerSecrets()</c>; override with a custom provider only
/// when sourcing per-tenant. Injects nothing (containment still holds) when a session declares no needs.
/// </summary>
public sealed class HostCredentialsBrokerSecretsProvider(
    IOptions<SandboxOptions> options,
    ILogger<HostCredentialsBrokerSecretsProvider> logger) : ISandboxBrokerSecretsProvider
{
    public Task<SandboxBrokerSecrets?> ResolveAsync(
        SandboxSessionRequest request, SandboxProfile profile, CancellationToken ct = default)
    {
        var loc = options.Value.BrokerCredentials;
        var needs = request.Broker;
        var secrets = new SandboxBrokerSecrets();

        if (needs is null)
        {
            logger.LogWarning(
                "Broker session {Name} declared no needs (request.Broker is null) — injecting no credentials", request.Name);
            return Task.FromResult<SandboxBrokerSecrets?>(secrets);
        }

        // Model auth — ONLY the providers this session's tool talks to (least privilege). Unknown provider names
        // and providers with no configured/readable credential are skipped rather than failing the launch.
        foreach (var provider in needs.ModelProviders)
        {
            var upstream = provider.Trim().ToLowerInvariant() switch
            {
                "anthropic" when SandboxCredentialSources.AnthropicOAuth(loc.AnthropicDir) is { } oat
                    => ModelUpstreamSpec.AnthropicOAuth(oat),
                "openai" when SandboxCredentialSources.OpenAiApiKey(loc.OpenAiDir) is { } key
                    => ModelUpstreamSpec.OpenAiApiKey(key),
                _ => null,
            };
            if (upstream is not null)
                secrets = secrets.WithModel(upstream);
            else
                logger.LogWarning(
                    "Broker session {Name} needs provider '{Provider}' but no credential is configured/readable for it",
                    request.Name, provider);
        }

        // Git credentials — served on demand by the broker's git mint when the session may clone/push.
        if (needs.Git && SandboxCredentialSources.GitCredentialLines(loc.GitDir) is { Count: > 0 } gitLines)
            secrets = secrets.WithGitCredentials(string.Join('\n', gitLines));

        // GitHub token (Copilot CLI) — from config; there is no standard on-disk source for it.
        if (needs.GitHub && !string.IsNullOrWhiteSpace(loc.GitHubToken))
            secrets = secrets.WithGitHubToken(loc.GitHubToken!);

        return Task.FromResult<SandboxBrokerSecrets?>(secrets);
    }
}
