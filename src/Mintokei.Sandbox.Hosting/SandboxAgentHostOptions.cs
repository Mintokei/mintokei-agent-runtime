namespace Mintokei.Sandbox.Hosting;

/// <summary>
/// Host-wide defaults for <see cref="SandboxAgentHost"/>, bound from the <c>SandboxAgentHost</c> config
/// section. These are the values that are the same for every run (where the sandbox dials back, which
/// credentials to seed, how long to wait); per-run choices live on <see cref="SandboxAgentRequest"/>.
/// </summary>
/// <remarks>
/// Only <see cref="BackendUrl"/> is really mandatory: it is the URL the runner INSIDE the container dials,
/// so it must be reachable from there — not <c>localhost</c> unless the sandbox shares the host's network.
/// On Docker Desktop / a dev box use <c>http://host.docker.internal:&lt;port&gt;/api</c> together with
/// <see cref="AddHostGateway"/>; in Kubernetes use the Service or ingress URL.
/// </remarks>
public sealed class SandboxAgentHostOptions
{
    public const string Section = "SandboxAgentHost";

    /// <summary>REST base URL the in-container runner enrolls against (must be reachable FROM the sandbox).</summary>
    public string? BackendUrl { get; set; }

    /// <summary>gRPC base URL for the control stream. Falls back to <see cref="BackendUrl"/> when unset —
    /// correct whenever the same endpoint serves HTTP/2 (an ingress with ALPN h2 does).</summary>
    public string? GrpcBackendUrl { get; set; }

    /// <summary>Docker only, dev convenience: add <c>--add-host=host.docker.internal:host-gateway</c> so a
    /// container can reach a host-local backend. Ignored by the Kubernetes backend.</summary>
    public bool AddHostGateway { get; set; }

    /// <summary>Isolation profile used when a run doesn't name one (see <c>Sandbox:Profiles</c>).</summary>
    public string Profile { get; set; } = "standard";

    /// <summary>How long to wait for the in-container runner to enroll and connect before giving up.
    /// Image pulls dominate a cold first run, so keep this generous.</summary>
    public int OnlineTimeoutSeconds { get; set; } = 180;

    /// <summary>How often to poll the container while waiting, to notice an early exit (failed clone, bad
    /// credentials) instead of burning the whole timeout.</summary>
    public int StatusPollMilliseconds { get; set; } = 1000;

    // Credential seeding — host paths mounted read-only into the sandbox, copied to a writable HOME by the
    // entrypoint. Leave null to run with no agent credentials (fine for a smoke test, not for real work).
    // Ignored for broker-egress profiles: there the broker injects credentials and the box never sees them.

    /// <summary>Host <c>~/.claude</c> (Claude Code credentials + settings).</summary>
    public string? ClaudeConfigHostDir { get; set; }

    /// <summary>Host <c>~/.claude.json</c>.</summary>
    public string? ClaudeConfigJsonHostFile { get; set; }

    /// <summary>Host <c>~/.codex</c> (Codex credentials + settings).</summary>
    public string? CodexConfigHostDir { get; set; }

    /// <summary>Host dir holding <c>.git-credentials</c> (+ optional <c>.ssh/</c>) for private-repo clones.</summary>
    public string? GitCredentialsHostDir { get; set; }

    /// <summary>Optional bare-repo mirror mounted read-only at <c>/repo-cache</c>; clones borrow its objects.</summary>
    public string? RepoCacheHostPath { get; set; }
}
