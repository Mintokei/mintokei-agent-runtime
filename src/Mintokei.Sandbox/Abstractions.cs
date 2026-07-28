namespace Mintokei.Sandbox;

/// <summary>A provisioned sandbox — one container per agent session.</summary>
public sealed record SandboxHandle(string Id, string Name, string Backend);

/// <summary>
/// The non-root uid/gid the sandbox image runs as (Dockerfile.sandbox: <c>groupadd/useradd -u 10001 agent</c>
/// + <c>USER agent</c>). Both backends pin scratch-dir ownership and <c>RunAsUser</c> to it so the agent can
/// write its <c>--data-dir</c>; keep this constant and the Dockerfile in lockstep.
/// </summary>
public static class SandboxImage
{
    public const long AgentUid = 10001;

    /// <summary>The non-root uid/gid the broker image runs as (Dockerfile.broker: <c>useradd -u 10002 broker</c>).
    /// Credentials staged for the broker to read (nested-runner broker) are chown'd to this so the broker can read
    /// them while staying non-root; keep in lockstep with Dockerfile.broker + <c>KubernetesBrokerSpec</c>.</summary>
    public const long BrokerUid = 10002;

    /// <summary>The agent user's HOME (Dockerfile.sandbox: <c>useradd -m</c> + <c>ENV HOME</c>). Under a
    /// read-only rootfs it must be a writable tmpfs so the entrypoint can seed creds and the CLIs can write.</summary>
    public const string AgentHome = "/home/agent";
}

public enum SandboxState { Pending, Running, Exited, NotFound, Unknown }

public sealed record SandboxStatus(SandboxState State, int? ExitCode = null, string? Detail = null);

/// <summary>cgroup caps applied per session (bounds one runaway session; predictable host usage).</summary>
public sealed record SandboxResourceLimits(long MemoryBytes, double Cpus, int PidsLimit);

public sealed record SandboxMount(string Source, string Target, bool ReadOnly);

/// <summary>
/// Egress posture for a session. <c>Open</c>: unrestricted network + credentials seeded into the box.
/// <c>Proxy</c>: route through an allowlisting HTTP CONNECT proxy (advisory — honoured only by clients that
/// obey <c>HTTP(S)_PROXY</c>), creds still seeded. <c>Broker</c>: deny-by-default egress that the sandbox can
/// only leave through a per-session broker, which injects short-lived, scoped credentials — so no long-lived
/// secret is ever seeded into the container (<see cref="SandboxSpec.EgressAllowlist"/> bounds where it may go).
/// </summary>
public enum SandboxEgress { Open, Proxy, Broker }

/// <summary>
/// Everything needed to launch one sandbox. <see cref="RuntimeClass"/> is the OCI runtime the
/// isolation profile selects ("runc" | "runsc" | "kata-fc"); both the Docker and (future)
/// Kubernetes backends map it to a single knob (<c>--runtime</c> / <c>runtimeClassName</c>).
/// </summary>
public sealed record SandboxSpec
{
    public required string Image { get; init; }
    public required string Name { get; init; }
    public required string RuntimeClass { get; init; }
    public required SandboxResourceLimits Limits { get; init; }
    public SandboxEgress Egress { get; init; } = SandboxEgress.Open;
    public string? EgressProxyUrl { get; init; }

    /// <summary>
    /// Hostnames the session is allowed to reach in <see cref="SandboxEgress.Broker"/> mode (git host, package
    /// registries, model API, the control-plane backend). Everything else is denied. Empty in Open/Proxy mode.
    /// </summary>
    public IReadOnlyList<string> EgressAllowlist { get; init; } = [];

    /// <summary>
    /// The agent tools this sandbox was provisioned to serve. Once a sandbox can host MORE THAN ONE session,
    /// this is what stops the second one widening the first one's blast radius: the egress allowlist and the
    /// injected model credentials are per-SANDBOX (one broker, one network — it cannot tell which session a
    /// connection came from), so admitting a tool the sandbox was not built for silently grants every session
    /// in it that tool's reach.
    ///
    /// Empty means "unconstrained" — the single-session behaviour, where the sandbox serves exactly the one
    /// session it was provisioned for and there is nothing to admit. Enforced by
    /// <see cref="SandboxAdmission"/>, which fails closed.
    /// </summary>
    public IReadOnlyList<string> AdmittedTools { get; init; } = [];

    /// <summary>
    /// Per-session Docker network the container joins — the deny-by-default <c>--internal</c> net in
    /// <see cref="SandboxEgress.Broker"/> mode (its only exit is the session broker). Null = the daemon's
    /// default bridge (Open/Proxy). Required when <see cref="Egress"/> is <see cref="SandboxEgress.Broker"/>.
    /// </summary>
    public string? NetworkName { get; init; }

    public IReadOnlyList<SandboxMount> Mounts { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>Args appended after the image — passed to the container entrypoint (the runner flags).</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>tmpfs targets (ephemeral, e.g. the runner data dir "/data").</summary>
    public IReadOnlyList<string> Tmpfs { get; init; } = ["/data"];

    /// <summary>
    /// Mount the container root filesystem read-only (Docker <c>--read-only</c> / K8s
    /// <c>readOnlyRootFilesystem</c>). Opt-in hardening: it only adds defence over the non-root default for
    /// paths the agent user could otherwise write in the image, and it breaks agents that write outside the
    /// writable <see cref="Tmpfs"/> set (e.g. <c>apt-get</c>, build tools writing to system dirs) — so it is a
    /// per-profile choice, off by default. When on, HOME / <c>/tmp</c> / the repos root are made writable tmpfs.
    /// </summary>
    public bool ReadOnlyRootfs { get; init; }

    /// <summary>Add host.docker.internal → host-gateway (dev only; prod reaches a real ingress).</summary>
    public bool AddHostGateway { get; init; }

    /// <summary>
    /// The secrets a per-session broker should hold and inject in <see cref="SandboxEgress.Broker"/> mode (git
    /// creds, model upstreams, a GitHub token for Copilot) — NEVER seeded into the box. Carried on the spec so
    /// the provisioning backend can start the broker; the Docker worker path passes secrets to
    /// <c>RemoteSandboxManager.LaunchAsync</c> directly instead. Null → the broker runs deny-by-default with no
    /// injected credentials.
    /// </summary>
    public SandboxBrokerSecrets? BrokerSecrets { get; init; }

    /// <summary>
    /// When set, the session's repo root (<c>/repos</c>) is backed by a PERSISTENT workspace store keyed by this
    /// id (K8s: a PVC <c>mintokei-ws-&lt;key:N&gt;</c>; nested Docker: the named volume of the same name). Survives
    /// a Pod/container recycle so a reaped-then-resumed session keeps its working tree AND the agent-CLI
    /// transcript (symlinked onto /repos by the entrypoint) — the difference between "re-provisions" and
    /// "actually resumes". Null → ephemeral /repos.
    /// <para>The key is OPAQUE: the runtime only names and labels the store with it. Key by whatever owns the
    /// working tree in your product — one per task, or one per workspace shared by every task in it.</para>
    /// </summary>
    public Guid? PersistentWorkspaceKey { get; init; }
}

/// <summary>
/// A runtime that persists a workspace store (a K8s PVC / a Docker named volume) at <c>/repos</c> so a
/// reaped-then-re-provisioned session keeps its working tree + CLI transcript. Implemented by backends that
/// support persistence; the embedder's reaper GCs a store once whatever it is keyed by is finished.
/// </summary>
public interface ISandboxWorkspaceStore
{
    /// <summary>Keys that currently have a persistent workspace store on this backend.</summary>
    Task<IReadOnlyList<Guid>> ListPersistentWorkspaceKeysAsync(CancellationToken ct = default);

    /// <summary>
    /// Remove a key's persistent workspace store. Tolerant when it is already gone. Returns true only when
    /// the store is genuinely GONE afterwards, so a caller that mirrors the deletion into its own state
    /// (dropping a stored session id whose transcript lived on it) never acts on a store that survived.
    /// </summary>
    Task<bool> RemovePersistentWorkspaceAsync(Guid key, CancellationToken ct = default);
}

/// <summary>Deterministic naming for the persistent workspace store, shared by the backends + the reaper.</summary>
public static class SandboxWorkspaceStore
{
    /// <summary>Name prefix + label key. Both are PERSISTED IN THE INFRASTRUCTURE (volume/PVC names and
    /// labels of stores that already exist), so their VALUES must not change — renaming them would orphan
    /// every existing store: the reaper selects on this label and would stop seeing them. The label reads
    /// "task" for that historical reason; the key itself is opaque.</summary>
    public const string NamePrefix = "mintokei-ws-";
    public const string LabelKey = "mintokei.task";

    public static string Name(Guid key) => NamePrefix + key.ToString("N");

    public static bool TryParseKey(string name, out Guid key)
    {
        key = default;
        return name.StartsWith(NamePrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(name[NamePrefix.Length..], "N", out key);
    }
}

/// <summary>
/// Launches / stops / inspects sandbox containers. One implementation per backend
/// ("docker" now, "k8s" later); the Sandbox Manager's pool + lifecycle logic is written
/// once against this seam and does not know which backend is live.
/// </summary>
public interface ISandboxRuntime
{
    /// <summary>"docker" | "k8s".</summary>
    string Backend { get; }

    Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default);
    Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct = default);
    Task StopAsync(SandboxHandle handle, CancellationToken ct = default);

    /// <summary>
    /// Every sandbox this runtime currently manages (running or exited), regardless of whether the pool
    /// still tracks it — used to reconcile after a process restart. Backend-agnostic: Docker filters by
    /// label, Kubernetes would filter Pods by the same label.
    /// </summary>
    Task<IReadOnlyList<SandboxHandle>> ListManagedAsync(CancellationToken ct = default);
}

/// <summary>
/// Optional capability a backend may add on top of <see cref="ISandboxRuntime"/>: fetch a sandbox's
/// recent log output. Used to explain WHY a sandbox that never enrolled failed — almost always a repo
/// clone / git-credentials error in the entrypoint, which is otherwise invisible once the (single-shot)
/// container is reaped. Kept OFF <see cref="ISandboxRuntime"/> so lightweight test doubles need not
/// implement it; callers feature-detect with <c>runtime is ISandboxLogSource</c>.
/// </summary>
public interface ISandboxLogSource
{
    /// <summary>
    /// Tail of the sandbox's combined stdout/stderr, best-effort. Returns an empty string when logs are
    /// unavailable (container already gone, backend error) — never throws, so a failure-reporting path can
    /// call it without its own try/catch.
    /// </summary>
    Task<string> GetLogsAsync(SandboxHandle handle, int tailLines = 40, CancellationToken ct = default);
}

public sealed class SandboxRuntimeException(string message, Exception? inner = null)
    : Exception(message, inner);
