namespace RemoteSandboxMinimal;

/// <summary>
/// Demo settings for <c>/demo/remote-sandbox-run</c>, bound from the <c>Sandbox</c> section (appsettings,
/// env vars like <c>Sandbox__BackendUrl</c>, CLI args). The URLs are what the sandbox container — running on
/// the REMOTE worker — dials back to reach this backend, so they must be reachable from that worker's Docker
/// (a LAN address, not <c>localhost</c>, when the worker is a different machine). Credentials are optional;
/// unset ⇒ each defaults to the worker's own <c>~/.claude</c> etc. (probed at run time).
/// </summary>
public sealed class RemoteDemoOptions
{
    public const string Section = "Sandbox";

    /// <summary>REST enroll URL the container's runner dials — reachable from the WORKER.</summary>
    public string BackendUrl { get; set; } = "http://host.docker.internal:5084";

    /// <summary>gRPC control-stream URL the container's runner dials — reachable from the WORKER.</summary>
    public string GrpcBackendUrl { get; set; } = "http://host.docker.internal:5085";

    /// <summary>Dev-only: add <c>host.docker.internal → host-gateway</c> to the container (only helps when the
    /// worker is on the SAME host as this backend). For a truly-remote worker, set reachable URLs above and
    /// leave this false.</summary>
    public bool AddHostGateway { get; set; } = true;

    // Optional credential sources ON THE WORKER. Unset ⇒ the worker's own ~/.claude / ~/.codex / git creds
    // (probed via $HOME). Whatever these resolve to is STAGED into a uid-readable copy on the worker so the
    // non-root sandbox can read it (SandboxCredentialStager) — the security piece this sample demonstrates.
    public string? ClaudeConfigHostDir { get; set; }
    public string? ClaudeConfigJsonHostFile { get; set; }
    public string? CodexConfigHostDir { get; set; }
    public string? GitCredentialsHostDir { get; set; }
}
