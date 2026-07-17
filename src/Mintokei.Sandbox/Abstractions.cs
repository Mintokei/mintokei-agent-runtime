namespace Mintokei.Sandbox;

/// <summary>A provisioned sandbox — one container per agent session.</summary>
public sealed record SandboxHandle(string Id, string Name, string Backend);

public enum SandboxState { Pending, Running, Exited, NotFound, Unknown }

public sealed record SandboxStatus(SandboxState State, int? ExitCode = null, string? Detail = null);

/// <summary>cgroup caps applied per session (bounds one runaway session; predictable host usage).</summary>
public sealed record SandboxResourceLimits(long MemoryBytes, double Cpus, int PidsLimit);

public sealed record SandboxMount(string Source, string Target, bool ReadOnly);

public enum SandboxEgress { Open, Proxy }

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
    public IReadOnlyList<SandboxMount> Mounts { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>Args appended after the image — passed to the container entrypoint (the runner flags).</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>tmpfs targets (ephemeral, e.g. the runner data dir "/data").</summary>
    public IReadOnlyList<string> Tmpfs { get; init; } = ["/data"];

    /// <summary>Add host.docker.internal → host-gateway (dev only; prod reaches a real ingress).</summary>
    public bool AddHostGateway { get; init; }
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

public sealed class SandboxRuntimeException(string message, Exception? inner = null)
    : Exception(message, inner);
