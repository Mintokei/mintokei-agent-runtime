namespace BrokerSandboxMinimal;

/// <summary>
/// Demo settings for <c>/demo/broker-sandbox-run</c>, bound from the <c>Sandbox</c> section. Unlike the
/// seeding sample, broker mode puts NO credential on the sandbox: the secrets below are handed to the
/// per-session broker on the worker, which injects them on demand (git) / re-originates with them (model).
///
/// Broker-mode constraints (enforced by the runtime, see the README):
/// the runner reaches the backend THROUGH the broker's TLS-only CONNECT proxy, so the gRPC/backend URL must be
/// <c>https://</c> and the backend host must appear in the profile's <c>EgressAllowlist</c> (appsettings).
/// </summary>
public sealed class BrokerDemoOptions
{
    public const string Section = "Sandbox";

    /// <summary>Run the broker + sandbox on THIS machine's Docker with no enrolled worker
    /// (<c>AddMintokeiLocalCommandRunner</c>). When true, <c>host</c> is ignored and no worker need connect —
    /// see the "full local loop" section of the README. Default false = dispatch to a connected remote worker.</summary>
    public bool LocalDocker { get; set; }

    /// <summary>REST enroll URL the container's runner dials — must be https and egress-allowlisted.</summary>
    public string BackendUrl { get; set; } = "https://backend.example.com";

    /// <summary>gRPC control-stream URL the container's runner dials — must be https and egress-allowlisted.</summary>
    public string GrpcBackendUrl { get; set; } = "https://backend.example.com";

    /// <summary>Git credentials the BROKER injects on demand: <c>"host=user:token"</c> lines (e.g.
    /// <c>github.com=x-access-token:ghp_...</c>). Prefer a short-lived, repo-scoped token. Held on the worker
    /// by the broker; never seeded into the sandbox.</summary>
    public string? GitCredentials { get; set; }

    /// <summary>Anthropic subscription (Max/Pro) OAuth access token (<c>sk-ant-oat…</c>, e.g. from
    /// <c>~/.claude/.credentials.json</c>). <see cref="DemoBrokerSecrets"/> turns it into the right injected
    /// header via <see cref="Mintokei.Sandbox.ModelUpstreamSpec.AnthropicOAuth"/> — no hand-formatting. Held on
    /// the worker; never seeded into the sandbox. (For a raw x-api-key or another provider, build a
    /// <c>ModelUpstreamSpec</c> directly.)</summary>
    public string? AnthropicOAuthToken { get; set; }

    /// <summary>Optional GitHub token minted for the Copilot CLI's GitHub API calls (fine-grained
    /// <c>github_pat_…</c>). Held on the worker; never seeded into the sandbox.</summary>
    public string? GitHubToken { get; set; }
}
