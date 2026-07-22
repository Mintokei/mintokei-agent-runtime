namespace Mintokei.Sandbox;

/// <summary>
/// Applies a resolved <see cref="BrokerEndpoint"/> to a <see cref="SandboxSpec"/>'s env — backend-agnostic, so
/// the Docker (worker) and Kubernetes paths wire the sandbox identically: <c>NO_PROXY</c> the broker host (its
/// git-mint/model/github services are PLAINTEXT and must NOT go through the CONNECT proxy the backend also sets),
/// point each configured model provider's base URL at its broker port + seed a placeholder credential, point
/// Copilot's GitHub API at the broker + a <c>github_pat_</c> placeholder, and hand over the git-mint URL. The
/// broker overwrites every injected credential before it leaves, so none of the placeholders is a real secret.
/// </summary>
public static class SandboxBrokerWiring
{
    /// <summary>Sentinel model credential so the agent CLI ATTEMPTS its call (it won't without one); the broker
    /// replaces the auth header upstream, so this never leaves the box.</summary>
    public const string PlaceholderModelCredential = "mintokei-broker-injects-the-real-credential";

    /// <summary>GitHub-token placeholder for Copilot, which validates the FORMAT locally (rejects classic
    /// <c>ghp_</c>) before any network call; the broker overwrites the auth on Copilot's GitHub API calls.</summary>
    public const string PlaceholderGitHubToken =
        "github_pat_11BROKERINJECTS0000000_brokerReplacesThisPlaceholderWithTheRealGitHubTokenXXXX";

    public static SandboxSpec Apply(SandboxSpec spec, BrokerEndpoint e)
    {
        var env = new Dictionary<string, string>(spec.Env) { ["MINTOKEI_BROKER_CRED_URL"] = e.GitMintUrl };

        // The broker host must NOT be reached through the CONNECT proxy: the git-mint / model / github services on
        // it are PLAINTEXT, but the backend sets HTTP(S)_PROXY to the broker's CONNECT proxy. A client that honors
        // HTTP_PROXY (Claude Code / undici) would otherwise forward the plaintext call THROUGH the CONNECT proxy,
        // which only does CONNECT → 501/hang. Exempt the broker host; external egress still flows through it.
        env["NO_PROXY"] = env["no_proxy"] = e.ContainerName;

        // Point each configured provider's base URL at its broker port + seed a placeholder credential (only the
        // configured providers). Each provider has its own port, so one broker can serve several at once.
        if (e.ModelUrls is { } modelUrls)
            foreach (var (provider, url) in modelUrls)
            {
                if (ModelProviders.Find(provider) is not { } p) continue;
                env[p.BaseUrlVar] = url;
                env.TryAdd(p.CredentialVar, PlaceholderModelCredential);
            }

        // GitHub-token mint (Copilot CLI): point its GitHub API at the broker + a format-valid placeholder.
        if (e.GitHubApiUrl is { } githubApiUrl)
        {
            env["COPILOT_DEBUG_GITHUB_API_URL"] = githubApiUrl;
            env.TryAdd("COPILOT_GITHUB_TOKEN", PlaceholderGitHubToken);
        }

        return spec with { NetworkName = e.NetworkName, EgressProxyUrl = e.ProxyUrl, Env = env };
    }
}
