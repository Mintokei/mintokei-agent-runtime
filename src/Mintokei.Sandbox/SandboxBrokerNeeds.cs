namespace Mintokei.Sandbox;

/// <summary>
/// What one brokered session needs, in the sandbox layer's OWN vocabulary — model PROVIDER names (see
/// <see cref="ModelProviders"/>), not agent tools (which live a layer up). The product resolves its per-session
/// tool into this (which providers to inject, whether git / a GitHub token, and the tight egress allowlist) and
/// hangs it on <see cref="SandboxSessionRequest.Broker"/>; the runtime then injects only these and bounds egress
/// to <see cref="Allowlist"/>. Keeping this product-neutral is what lets one <c>broker</c> profile serve every
/// tool with a per-session credential + network posture instead of a per-tool profile or an allow-all list.
/// </summary>
/// <param name="ModelProviders">Provider names to inject a credential for (e.g. <c>["anthropic"]</c>).</param>
/// <param name="Git">Inject git credentials (served on demand by the broker's git mint).</param>
/// <param name="GitHub">Mint a GitHub token for the Copilot CLI.</param>
/// <param name="Allowlist">The session's egress allowlist; wins over the profile's when set (else the profile's).</param>
public sealed record SandboxBrokerNeeds(
    IReadOnlyList<string> ModelProviders,
    bool Git = false,
    bool GitHub = false,
    IReadOnlyList<string>? Allowlist = null);
