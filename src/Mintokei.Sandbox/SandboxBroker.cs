namespace Mintokei.Sandbox;

/// <summary>One model provider the broker injects for: the CLI provider it serves (<c>"anthropic"</c>,
/// <c>"openai"</c> — see <see cref="ModelProviders"/>), the real upstream base URL (e.g.
/// <c>https://api.anthropic.com</c>), and the auth header(s) to inject (<c>Name: value</c> / <c>Name=value</c>
/// lines). The sandbox never holds the auth — the broker re-originates the call over TLS with it added.</summary>
public sealed record ModelUpstreamSpec(string Provider, string Upstream, string? Auth = null);

/// <summary>
/// Secret material the broker holds on the worker for one session — NEVER seeded into the sandbox. Git
/// credentials as <c>"host=user:token"</c> lines (served on demand via the git-credential mint), and/or one or
/// more model-API upstreams + auth header(s) to inject (re-originated over TLS). All optional.
/// </summary>
public sealed record SandboxBrokerSecrets(
    string? GitCredentials = null,
    string? ModelUpstream = null,
    string? ModelAuth = null,
    IReadOnlyList<ModelUpstreamSpec>? ModelUpstreams = null)
{
    /// <summary>The providers the broker injects for: the explicit <see cref="ModelUpstreams"/> if any, else the
    /// legacy scalar <see cref="ModelUpstream"/>/<see cref="ModelAuth"/> normalized to a single <c>"anthropic"</c>
    /// upstream (back-compat). Empty when no model injection is configured.</summary>
    public IReadOnlyList<ModelUpstreamSpec> EffectiveModelUpstreams =>
        ModelUpstreams is { Count: > 0 } list ? list
        : !string.IsNullOrWhiteSpace(ModelUpstream) ? [new ModelUpstreamSpec("anthropic", ModelUpstream!, ModelAuth)]
        : [];
}

/// <summary>What to launch a broker for: the session, its egress allowlist, and the secrets it should inject.</summary>
public sealed record SandboxBrokerRequest(string SessionName, IReadOnlyList<string> EgressAllowlist, SandboxBrokerSecrets? Secrets = null);

/// <summary>
/// How the sandbox reaches its broker. The sandbox joins <see cref="NetworkName"/> (the deny-by-default
/// <c>--internal</c> net) and routes egress through <see cref="ProxyUrl"/>; the in-sandbox git helper calls
/// <see cref="GitMintUrl"/>; each configured model provider's base URL points at its entry in
/// <see cref="ModelUrls"/> (provider name → broker URL) when model injection is configured. All URLs address the
/// broker by its container name on the internal network.
/// </summary>
public sealed record BrokerEndpoint(
    string NetworkName, string ContainerName, string ProxyUrl, string GitMintUrl,
    IReadOnlyDictionary<string, string>? ModelUrls = null);

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
