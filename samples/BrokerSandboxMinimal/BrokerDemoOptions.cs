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

    /// <summary>Optional model-API injection: the real upstream base URL (e.g. <c>https://api.anthropic.com</c>).
    /// When set, the sandbox's model base URL is pointed at the broker, which adds the key below.</summary>
    public string? ModelUpstream { get; set; }

    /// <summary>Auth header(s) the broker injects into model calls: <c>Name: value</c> / <c>Name=value</c>
    /// (e.g. <c>x-api-key=sk-ant-...</c>). Held on the worker; never seeded into the sandbox.</summary>
    public string? ModelAuth { get; set; }
}
