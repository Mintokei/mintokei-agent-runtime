namespace Mintokei.Sandbox;

/// <summary>
/// The model providers the broker can inject for, and how each bridges the two sides: the base-URL env var the
/// agent CLI reads (so we can point it at the broker), the credential env var it needs SOME value in (the
/// runtime seeds a placeholder; the broker injects the real key), and the port the broker's reverse-proxy for
/// that provider listens on. Keyed by the provider name used in <see cref="ModelUpstreamSpec.Provider"/>.
///
/// Distinct ports are what let ONE broker serve several providers at once: the sandbox reaches
/// <c>ANTHROPIC_BASE_URL</c> on the anthropic port and <c>OPENAI_BASE_URL</c> on the openai port, each
/// terminated by its own upstream + auth. (Adding a provider here + a matching CLI env convention is all it
/// takes to broker another one.)
/// </summary>
public static class ModelProviders
{
    /// <param name="Name">Provider key used in <see cref="ModelUpstreamSpec.Provider"/> (case-insensitive).</param>
    /// <param name="BaseUrlVar">The env var the CLI reads for this provider's API base URL.</param>
    /// <param name="CredentialVar">The env var the CLI needs a (placeholder) credential in to attempt a call.</param>
    /// <param name="Port">The broker reverse-proxy port for this provider.</param>
    public sealed record Provider(string Name, string BaseUrlVar, string CredentialVar, int Port);

    /// <summary>All known providers. Ports must be distinct (one broker, several proxies).</summary>
    public static IReadOnlyList<Provider> All { get; } =
    [
        new("anthropic", "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", 3130),
        new("openai",    "OPENAI_BASE_URL",    "OPENAI_API_KEY",       3131),
    ];

    /// <summary>The provider named <paramref name="name"/> (case-insensitive), or null if unknown.</summary>
    public static Provider? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
