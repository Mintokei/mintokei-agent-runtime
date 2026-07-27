using Mintokei.AgentEngine;
using Mintokei.AgentEngine.AgentTools;

namespace Mintokei.Sandbox.Hosting;

/// <summary>
/// One sandboxed agent run: which CLI, what to work on, and where to isolate it. Everything is optional
/// except <see cref="Tool"/> — host-wide defaults (backend URLs, credentials, timeouts) come from
/// <see cref="SandboxAgentHostOptions"/>.
/// </summary>
public sealed record SandboxAgentRequest
{
    /// <summary>Which agent CLI runs inside the sandbox. The backend must be registered on the host
    /// (e.g. <c>.AddClaude()</c>), otherwise starting the session fails.</summary>
    public AgentToolKey Tool { get; init; } = AgentToolKey.ClaudeCodeCli;

    /// <summary>Optional first message. When set, it is sent as soon as the session is ready, so the caller
    /// can go straight to consuming <see cref="SandboxAgentRun.Output"/>.</summary>
    public string? Prompt { get; init; }

    /// <summary>Convenience single repo cloned into the sandbox. Combined with <see cref="Repos"/>.</summary>
    public string? Repo { get; init; }

    /// <summary>Repos to clone into the sandbox (each lands at <c>/repos/&lt;name&gt;</c>). Empty runs the
    /// agent against an empty workspace.</summary>
    public IReadOnlyList<SandboxRepoSpec> Repos { get; init; } = [];

    /// <summary>Working directory for the agent inside the container. Defaults to the first repo's checkout
    /// when a repo is provisioned, else the repo root (<c>/repos</c>).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Isolation profile (runtime class, cpu/memory caps, egress posture). Falls back to
    /// <see cref="SandboxAgentHostOptions.Profile"/>.</summary>
    public string? Profile { get; init; }

    /// <summary>
    /// Run the sandbox ON A CONNECTED WORKER instead of this host: the container is launched over the
    /// worker's control channel (nested Docker) rather than on the local Docker/Kubernetes backend.
    /// Requires <c>AddRemoteWorkers()</c> and a worker that is connected and Docker-capable.
    /// </summary>
    public Guid? HostMachineId { get; init; }

    /// <summary>Opaque key the control plane registers the session under — pass your own task/job id when you
    /// want to look the session up later. Null uses the session's own generated id.</summary>
    public Guid? SessionKey { get; init; }

    /// <summary>Broker egress for this run: which model providers the per-session broker injects and the tight
    /// allowlist it enforces (see <see cref="SandboxProvisionRequest.Broker"/>). Requires a broker-egress
    /// profile. Null runs with the profile's own egress posture.</summary>
    public SandboxBrokerNeeds? Broker { get; init; }

    /// <summary>Kubernetes only: back <c>/repos</c> with a per-id persistent volume so the working tree and
    /// the CLI transcript survive a pod recycle (i.e. the session can be resumed).</summary>
    public Guid? PersistentWorkspaceKey { get; init; }

    /// <summary>Session behaviour — most importantly the interaction mode (auto-approve vs surface
    /// permission prompts on the output stream). Null uses the engine default.</summary>
    public AgentSessionOptions? SessionOptions { get; init; }

    /// <summary>Overrides <see cref="SandboxAgentHostOptions.OnlineTimeoutSeconds"/> for this run.</summary>
    public TimeSpan? OnlineTimeout { get; init; }

    /// <summary>All repos for this run — <see cref="Repo"/> (if set) followed by <see cref="Repos"/>.</summary>
    internal IReadOnlyList<SandboxRepoSpec> AllRepos() =>
        string.IsNullOrWhiteSpace(Repo) ? Repos : [new SandboxRepoSpec(Repo), .. Repos];
}

/// <summary>Thrown when a sandboxed run cannot be started — the container never came online, exited during
/// startup, or the host is missing a registration. Carries the container's tail logs when there are any,
/// because the cause is almost always visible there (failed clone, bad credentials, missing image).</summary>
public sealed class SandboxAgentException(
    string message, string? containerLogs = null, Exception? inner = null,
    SandboxState? terminalState = null, int? exitCode = null)
    : Exception(message, inner)
{
    /// <summary>Tail of the sandbox's logs at failure time, when they could be read.</summary>
    public string? ContainerLogs { get; } = containerLogs;

    /// <summary>The sandbox's last observed state — <see cref="SandboxState.Exited"/> when it died during
    /// startup (as opposed to simply never becoming ready before the timeout). Null when unknown.</summary>
    public SandboxState? TerminalState { get; } = terminalState;

    /// <summary>Exit code when the container exited during startup. Null when it never exited.</summary>
    public int? ExitCode { get; } = exitCode;
}
