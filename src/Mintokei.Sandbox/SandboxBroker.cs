namespace Mintokei.Sandbox;

/// <summary>One model provider the broker injects for: the CLI provider it serves (<c>"anthropic"</c>,
/// <c>"openai"</c> — see <see cref="ModelProviders"/>), the real upstream base URL (e.g.
/// <c>https://api.anthropic.com</c>), and the auth header(s) to inject (<c>Name: value</c> / <c>Name=value</c>
/// lines). The sandbox never holds the auth — the broker re-originates the call over TLS with it added.</summary>
public sealed record ModelUpstreamSpec(string Provider, string Upstream, string? Auth = null)
{
    // ── Convention builders ───────────────────────────────────────────────────────────────────────────────
    // The provider-specific header shapes are the SAME for every consumer, so the library owns them here (a
    // product should never have to re-derive them). Only which token to pass is the product's concern.

    /// <summary>Anthropic subscription (Max/Pro) OAuth: the agent CLI's placeholder <c>Authorization</c> is
    /// replaced upstream with the real bearer token plus the OAuth beta flag — exactly what Claude Code sends
    /// for a subscription. Pass the <c>sk-ant-oat…</c> access token (e.g. from <c>~/.claude/.credentials.json</c>).</summary>
    public static ModelUpstreamSpec AnthropicOAuth(string oauthAccessToken, string upstream = "https://api.anthropic.com") =>
        new("anthropic", upstream, $"Authorization: Bearer {oauthAccessToken};anthropic-beta: oauth-2025-04-20");

    /// <summary>OpenAI (Codex) API key: the client's <c>Authorization: Bearer</c> is replaced upstream with the
    /// real key. Served on the openai broker port.</summary>
    public static ModelUpstreamSpec OpenAiApiKey(string apiKey, string upstream = "https://api.openai.com") =>
        new("openai", upstream, $"Authorization: Bearer {apiKey}");
}

/// <summary>
/// Secret material the broker holds on the worker for one session — NEVER seeded into the sandbox. Git
/// credentials as <c>"host=user:token"</c> lines (served on demand via the git-credential mint), one or more
/// model-API upstreams + auth header(s) to inject, and/or a GitHub token minted for the Copilot CLI (injected
/// on its GitHub API calls). All optional; all re-originated over TLS, none seeded into the box.
/// </summary>
public sealed record SandboxBrokerSecrets(
    string? GitCredentials = null,
    string? ModelUpstream = null,
    string? ModelAuth = null,
    IReadOnlyList<ModelUpstreamSpec>? ModelUpstreams = null,
    string? GitHubToken = null)
{
    /// <summary>The providers the broker injects for: the explicit <see cref="ModelUpstreams"/> if any, else the
    /// legacy scalar <see cref="ModelUpstream"/>/<see cref="ModelAuth"/> normalized to a single <c>"anthropic"</c>
    /// upstream (back-compat). Empty when no model injection is configured.</summary>
    public IReadOnlyList<ModelUpstreamSpec> EffectiveModelUpstreams =>
        ModelUpstreams is { Count: > 0 } list ? list
        : !string.IsNullOrWhiteSpace(ModelUpstream) ? [new ModelUpstreamSpec("anthropic", ModelUpstream!, ModelAuth)]
        : [];

    // ── Convention builders + fluent composition ──────────────────────────────────────────────────────────

    /// <summary>A git-credential line for the mint, in the broker's <c>host=user:token</c> form (what
    /// <c>BROKER_GIT_CREDS</c> takes). Join several with newlines for multiple hosts.</summary>
    public static string GitCredentialLine(string host, string username, string token) => $"{host}={username}:{token}";

    /// <summary>Add model upstream(s) the broker injects for — e.g. <see cref="ModelUpstreamSpec.AnthropicOAuth"/>.
    /// Supersedes the legacy scalar <see cref="ModelUpstream"/>/<see cref="ModelAuth"/> (folded in first).</summary>
    public SandboxBrokerSecrets WithModel(params ModelUpstreamSpec[] upstreams) =>
        this with { ModelUpstreams = [.. EffectiveModelUpstreams, .. upstreams], ModelUpstream = null, ModelAuth = null };

    /// <summary>Set the git-credential lines the mint serves (see <see cref="GitCredentialLine"/>).</summary>
    public SandboxBrokerSecrets WithGitCredentials(string credentials) => this with { GitCredentials = credentials };

    /// <summary>Set the GitHub token minted for the Copilot CLI's GitHub API calls.</summary>
    public SandboxBrokerSecrets WithGitHubToken(string token) => this with { GitHubToken = token };
}

/// <summary>What to launch a broker for: the session, its egress allowlist, and the secrets it should inject.</summary>
public sealed record SandboxBrokerRequest(string SessionName, IReadOnlyList<string> EgressAllowlist, SandboxBrokerSecrets? Secrets = null);

/// <summary>
/// How the sandbox reaches its broker. The sandbox joins <see cref="NetworkName"/> (the deny-by-default
/// <c>--internal</c> net) and routes egress through <see cref="ProxyUrl"/>; the in-sandbox git helper calls
/// <see cref="GitMintUrl"/>; each configured model provider's base URL points at its entry in
/// <see cref="ModelUrls"/> (provider name → broker URL); and the Copilot CLI's GitHub API base URL points at
/// <see cref="GitHubApiUrl"/> when a GitHub token is minted. All URLs address the broker by its container name
/// on the internal network.
/// </summary>
public sealed record BrokerEndpoint(
    string NetworkName, string ContainerName, string ProxyUrl, string GitMintUrl,
    IReadOnlyDictionary<string, string>? ModelUrls = null,
    string? GitHubApiUrl = null);

/// <summary>
/// Orchestrates a per-session broker: create the deny-by-default <c>--internal</c> network, run the broker
/// container (dual-homed so only IT reaches the outside), and tear both down. Opt-in; driven by
/// <see cref="RemoteSandboxManager"/> when a profile selects <see cref="SandboxEgress.Broker"/>.
/// </summary>
public interface ISandboxBroker
{
    /// <summary>Provision the network + broker for <paramref name="request"/> and return how the sandbox reaches it.</summary>
    Task<BrokerEndpoint> StartAsync(Guid workerId, SandboxBrokerRequest request, CancellationToken ct = default);

    /// <summary>Remove the broker container + its network. Best-effort; must not throw (called from cleanup paths).</summary>
    Task StopAsync(Guid workerId, BrokerEndpoint endpoint, CancellationToken ct = default);
}

/// <summary>
/// Product-supplied source of the per-session secrets a broker injects (model auth, git creds, GitHub token).
/// The runtime asks it at provision time whenever a profile selects <see cref="SandboxEgress.Broker"/>, so the
/// product maps the session (its tenant / workspace, keyed off <see cref="SandboxSessionRequest.Name"/>) to the
/// real credentials — the ONE piece the product-agnostic runtime can't own, because it depends on the product's
/// identity model and secret store. Build the returned value with the convention helpers on
/// <see cref="SandboxBrokerSecrets"/> / <see cref="ModelUpstreamSpec"/> so no product re-derives header formats.
///
/// Register an implementation with <c>AddMintokeiSandboxBrokerSecrets&lt;T&gt;()</c>. When none is registered the
/// runtime uses a no-op that returns <c>null</c>: broker mode still enforces network containment (deny-by-default
/// egress through the broker) but injects no credentials.
/// </summary>
public interface ISandboxBrokerSecretsProvider
{
    /// <summary>Resolve the broker secrets for <paramref name="request"/> under <paramref name="profile"/>, or
    /// <c>null</c> for none. Called only in broker mode; must be safe to call concurrently.</summary>
    Task<SandboxBrokerSecrets?> ResolveAsync(SandboxSessionRequest request, SandboxProfile profile, CancellationToken ct = default);
}
