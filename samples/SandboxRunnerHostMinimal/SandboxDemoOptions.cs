namespace SandboxRunnerHostMinimal;

/// <summary>
/// Demo request settings for <c>/demo/sandbox-run</c>, bound from the <c>Sandbox</c> configuration section
/// (appsettings, env vars like <c>Sandbox__BackendUrl</c>, CLI args). Defaults target a local Docker host
/// reaching this process via <c>host.docker.internal</c>. Credentials are optional; set them to authenticate
/// the in-container CLI so the agent turn can actually run.
/// </summary>
public sealed class SandboxDemoOptions
{
    public const string Section = "Sandbox";

    /// <summary>REST enroll URL the container's runner dials — must be reachable from inside the container.</summary>
    public string BackendUrl { get; set; } = "http://host.docker.internal:5082";

    /// <summary>gRPC control-stream URL the container's runner dials.</summary>
    public string GrpcBackendUrl { get; set; } = "http://host.docker.internal:5083";

    // Optional credential seeding: each host path is mounted RO at /seed and copied into the container's
    // HOME by the entrypoint. Unset → plumbing-only (the runner enrolls and the session dispatches, but the
    // CLI has no credentials to complete a turn).
    public string? ClaudeConfigHostDir { get; set; }
    public string? ClaudeConfigJsonHostFile { get; set; }
    public string? CodexConfigHostDir { get; set; }
    public string? GitCredentialsHostDir { get; set; }
}
