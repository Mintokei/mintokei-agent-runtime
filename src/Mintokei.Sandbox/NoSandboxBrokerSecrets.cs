namespace Mintokei.Sandbox;

/// <summary>
/// Default <see cref="ISandboxBrokerSecretsProvider"/> when a host registers none: injects nothing. Broker mode
/// still enforces network containment (deny-by-default egress via the broker), but the CLI gets only the
/// placeholders — so a host that wants real model/git/GitHub injection must register its own provider with
/// <c>AddMintokeiSandboxBrokerSecrets&lt;T&gt;()</c>.
/// </summary>
internal sealed class NoSandboxBrokerSecrets : ISandboxBrokerSecretsProvider
{
    public Task<SandboxBrokerSecrets?> ResolveAsync(
        SandboxSessionRequest request, SandboxProfile profile, CancellationToken ct = default) =>
        Task.FromResult<SandboxBrokerSecrets?>(null);
}
