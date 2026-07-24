using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mintokei.Sandbox;

/// <summary>
/// A file-REFERENCE <see cref="ISandboxBrokerSecretsProvider"/> for backends where the control plane can't read
/// the node creds — the Kubernetes broker: the API pod runs non-root and the node creds are <c>0600 root</c>, so
/// <see cref="HostCredentialsBrokerSecretsProvider"/> (which reads them) gets nothing. Instead of reading tokens,
/// this emits <c>${json:…}</c>/<c>${gitcreds:…}</c> REFERENCES plus the cred DIRS to mount; the broker resolves
/// them itself from a per-session, broker-uid-owned staged copy (see <c>KubernetesBrokerSpec</c>'s staging
/// initContainer), so the real token is read broker-side and never touches the API pod or a k8s Secret. Sources
/// the same <see cref="SandboxOptions.BrokerCredentials"/> LOCATIONS as the reading provider. Register with
/// <c>AddMintokeiHostCredentialsFileRefBrokerSecrets()</c>. Injects nothing when a session declares no needs.
/// </summary>
public sealed class HostCredentialsFileRefBrokerSecretsProvider(
    IOptions<SandboxOptions> options,
    ILogger<HostCredentialsFileRefBrokerSecretsProvider> logger) : ISandboxBrokerSecretsProvider
{
    // Ref paths AS SEEN INSIDE the broker container (the staged emptyDir mount targets).
    private const string ClaudeDir = "/creds/claude";
    private const string CodexDir = "/creds/codex";
    private const string GitDir = "/creds/git";

    public Task<SandboxBrokerSecrets?> ResolveAsync(
        SandboxSessionRequest request, SandboxProfile profile, CancellationToken ct = default)
    {
        var loc = options.Value.BrokerCredentials;
        var needs = request.Broker;
        if (needs is null)
        {
            logger.LogWarning(
                "Broker session {Name} declared no needs (request.Broker is null) — injecting no credentials", request.Name);
            return Task.FromResult<SandboxBrokerSecrets?>(new SandboxBrokerSecrets());
        }

        var upstreams = new List<ModelUpstreamSpec>();
        var mounts = new List<SandboxBrokerCredentialMount>();

        // Model auth — ONLY the providers this session's tool talks to. A configured cred DIR becomes a ${json:…}
        // ref + a mount; NO token is read here (the broker resolves it from its staged copy).
        foreach (var provider in needs.ModelProviders)
        {
            switch (provider.Trim().ToLowerInvariant())
            {
                case "anthropic" when !string.IsNullOrWhiteSpace(loc.AnthropicDir):
                    upstreams.Add(ModelUpstreamSpec.AnthropicOAuthFromFile($"{ClaudeDir}/.credentials.json"));
                    mounts.Add(new SandboxBrokerCredentialMount(loc.AnthropicDir!, ClaudeDir));
                    break;
                case "openai" when !string.IsNullOrWhiteSpace(loc.OpenAiDir):
                    upstreams.Add(ModelUpstreamSpec.OpenAiApiKeyFromFile($"{CodexDir}/auth.json"));
                    mounts.Add(new SandboxBrokerCredentialMount(loc.OpenAiDir!, CodexDir));
                    break;
                default:
                    logger.LogWarning(
                        "Broker session {Name} needs provider '{Provider}' but no cred dir is configured for it",
                        request.Name, provider);
                    break;
            }
        }

        var secrets = new SandboxBrokerSecrets(ModelUpstreams: upstreams);

        // Git — reshaped broker-side from the mounted store (${gitcreds:…}); token stays on the node.
        if (needs.Git && !string.IsNullOrWhiteSpace(loc.GitDir))
        {
            mounts.Add(new SandboxBrokerCredentialMount(loc.GitDir!, GitDir));
            secrets = secrets.WithGitCredentialsFromFile($"{GitDir}/.git-credentials");
        }

        // GitHub token (Copilot) is a CONFIG value, not a node file, so there's nothing to stage — inline it.
        if (needs.GitHub && !string.IsNullOrWhiteSpace(loc.GitHubToken))
            secrets = secrets.WithGitHubToken(loc.GitHubToken!);

        return Task.FromResult<SandboxBrokerSecrets?>(secrets with { CredentialMounts = mounts });
    }
}
