namespace Mintokei.Sandbox.Hosting;

/// <summary>
/// Host-wide settings for provisioning sandboxes, bound from the same <c>Sandbox</c> section as
/// <see cref="SandboxOptions"/> — so one section describes the sandbox layer end to end (which backend and
/// image, which profiles, where sandboxes dial back, which credentials to seed). Two options types binding
/// one section is deliberate: each ignores the keys it doesn't declare.
///
/// <para>These are the values that are the same for every run. Genuinely per-run choices live on
/// <see cref="SandboxProvisionRequest"/> (profile, repos, broker needs); anything else can still be shaped
/// by the <c>configure</c> hook on <see cref="SandboxProvisioner.ProvisionAsync"/>.</para>
/// </summary>
/// <remarks>
/// Only <see cref="BackendUrl"/> is really mandatory: it is the URL the runner INSIDE the container dials,
/// so it must be reachable from there — not <c>localhost</c> unless the sandbox shares the host's network.
/// On Docker Desktop / a dev box use <c>http://host.docker.internal:&lt;port&gt;</c> together with
/// <see cref="AddHostGateway"/>; in Kubernetes use the Service URL.
/// </remarks>
public sealed class SandboxAgentHostOptions
{
    /// <summary>Config section — shared with <see cref="SandboxOptions"/>.</summary>
    public const string Section = "Sandbox";

    /// <summary>REST base URL the in-container runner enrolls against (must be reachable FROM the sandbox).</summary>
    public string? BackendUrl { get; set; }

    /// <summary>gRPC base URL for the control stream. Falls back to <see cref="BackendUrl"/> when unset —
    /// correct whenever the same endpoint serves HTTP/2 (an ingress with ALPN h2 does).</summary>
    public string? GrpcBackendUrl { get; set; }

    /// <summary>
    /// Public (https) REST URL used INSTEAD of <see cref="BackendUrl"/> for broker-egress sessions. Broker
    /// egress routes the runner's dial-back through a CONNECT proxy that only tunnels TLS, so a brokered
    /// sandbox cannot reach a plaintext in-cluster URL — it has to use the public ingress. Unset → brokered
    /// sessions use <see cref="BackendUrl"/> like everything else.
    /// </summary>
    public string? PublicBackendUrl { get; set; }

    /// <summary>Public gRPC URL for broker-egress sessions. Falls back to <see cref="PublicBackendUrl"/>
    /// (a public ingress carries h2, so it serves gRPC too).</summary>
    public string? PublicGrpcBackendUrl { get; set; }

    /// <summary>Docker only, dev convenience: add <c>--add-host=host.docker.internal:host-gateway</c> so a
    /// container can reach a host-local backend. Ignored by the Kubernetes backend, and rejected with broker
    /// egress (host reachability defeats containment).</summary>
    public bool AddHostGateway { get; set; }

    /// <summary>Isolation profile used when a run doesn't name one (see <c>Sandbox:Profiles</c>).</summary>
    public string Profile { get; set; } = "standard";

    /// <summary>How long to wait for the in-container runner to enroll and connect before giving up.
    /// Image pulls dominate a cold first run, so keep this generous.</summary>
    public int OnDemandTimeoutSeconds { get; set; } = 180;

    /// <summary>How often to poll the container while waiting, to notice an early exit (failed clone, bad
    /// credentials) instead of burning the whole timeout.</summary>
    public int ProvisionStatusPollMs { get; set; } = 1000;

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
