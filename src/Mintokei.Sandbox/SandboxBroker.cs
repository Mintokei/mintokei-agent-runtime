namespace Mintokei.Sandbox;

/// <summary>
/// Secret material the broker holds on the worker for one session — NEVER seeded into the sandbox. Git
/// credentials as <c>"host=user:token"</c> lines (served on demand via the git-credential mint), and/or a
/// model-API upstream + auth header(s) to inject (re-originated over TLS). All optional.
/// </summary>
public sealed record SandboxBrokerSecrets(string? GitCredentials = null, string? ModelUpstream = null, string? ModelAuth = null);

/// <summary>What to launch a broker for: the session, its egress allowlist, and the secrets it should inject.</summary>
public sealed record SandboxBrokerRequest(string SessionName, IReadOnlyList<string> EgressAllowlist, SandboxBrokerSecrets? Secrets = null);

/// <summary>
/// How the sandbox reaches its broker. The sandbox joins <see cref="NetworkName"/> (the deny-by-default
/// <c>--internal</c> net) and routes egress through <see cref="ProxyUrl"/>; the in-sandbox git helper calls
/// <see cref="GitMintUrl"/>; the agent's model base URL points at <see cref="ModelUrl"/> when model injection
/// is configured. All URLs address the broker by its container name on the internal network.
/// </summary>
public sealed record BrokerEndpoint(string NetworkName, string ContainerName, string ProxyUrl, string GitMintUrl, string? ModelUrl);

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
