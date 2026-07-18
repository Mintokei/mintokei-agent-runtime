using Microsoft.Extensions.Options;

namespace Mintokei.Sandbox;

/// <summary>Config-bound sandbox settings (configuration section <c>Sandbox</c>).</summary>
public sealed class SandboxOptions
{
    public const string SectionName = "Sandbox";

    /// <summary>Container runtime backend: "docker" | "kubernetes" (alias "k8s"). Selects the
    /// <see cref="ISandboxRuntime"/> implementation at DI registration; one backend per host process.</summary>
    public string Backend { get; set; } = "docker";

    /// <summary>Namespace the Kubernetes backend creates sandbox Pods in. Ignored by the Docker backend.</summary>
    public string KubernetesNamespace { get; set; } = "default";

    /// <summary>Pull policy for sandbox Pods ("Always" | "IfNotPresent" | "Never"). Null = the Kubernetes
    /// default (Always for a ":latest" tag, else IfNotPresent). Set "Never" when the sandbox image is
    /// node-imported rather than pulled from a registry, so a ":latest" tag doesn't trigger a failing pull
    /// of a private image. Ignored by the Docker backend.</summary>
    public string? KubernetesImagePullPolicy { get; set; }

    // --- Which cluster the Kubernetes backend targets. All null = default: in-cluster ServiceAccount when
    // the API runs as a Pod, else the ambient kubeconfig. Set these to point sandbox Pods at a SEPARATE /
    // dedicated cluster (decouples the sandbox substrate from where the control plane runs). Ignored by the
    // Docker backend. Precedence: explicit server+token > kubeconfig file > in-cluster/ambient.

    /// <summary>Path to a kubeconfig file the backend uses to reach the sandbox cluster (overrides in-cluster).
    /// The usual way to target a remote/dedicated cluster: mount a kubeconfig and point here.</summary>
    public string? KubernetesKubeconfig { get; set; }

    /// <summary>Context to select within the kubeconfig (null = the file's current-context).</summary>
    public string? KubernetesContext { get; set; }

    /// <summary>Explicit API-server URL for the sandbox cluster (alternative to a kubeconfig file). When set,
    /// pair with <see cref="KubernetesToken"/>.</summary>
    public string? KubernetesApiServerUrl { get; set; }

    /// <summary>Bearer token for <see cref="KubernetesApiServerUrl"/>.</summary>
    public string? KubernetesToken { get; set; }

    /// <summary>Skip API-server TLS verification for the explicit-server path (dev / self-signed only —
    /// prefer a kubeconfig, which carries the cluster CA, for real clusters).</summary>
    public bool KubernetesSkipTlsVerify { get; set; }

    /// <summary>Sandbox image reference.</summary>
    public string Image { get; set; } = "mintokei/sandbox:latest";

    /// <summary>Profile used when neither the session nor the workspace overrides it.</summary>
    public string DefaultProfile { get; set; } = "standard";

    /// <summary>Profiles a caller may request. A request outside this set is clamped to the default.</summary>
    public List<string> AllowedProfiles { get; set; } = ["standard"];

    /// <summary>Named isolation profiles. When empty the resolver falls back to a built-in standard/runc tier.</summary>
    public Dictionary<string, SandboxProfileConfig> Profiles { get; set; } = new();

    /// <summary>Warm (repo-agnostic) default-profile sandboxes the pool keeps online. 0 = no warm pool.</summary>
    public int WarmPoolSize { get; set; }

    /// <summary>Seconds between warm-pool maintenance ticks (top-up + reap). Minimum 1.</summary>
    public int PoolIntervalSeconds { get; set; } = 15;
}

/// <summary>One isolation tier: an OCI runtime + resource caps + egress posture.</summary>
public sealed class SandboxProfileConfig
{
    /// <summary>OCI runtime class: "runc" (standard) | "runsc" (gVisor) | "kata-fc" (Firecracker microVM).</summary>
    public string Runtime { get; set; } = "runc";
    public int MemoryMb { get; set; } = 4096;
    public double Cpus { get; set; } = 2;
    public int PidsLimit { get; set; } = 512;

    /// <summary>"open" | "proxy" (allowlist egress via an HTTP CONNECT proxy).</summary>
    public string Egress { get; set; } = "open";
    public string? EgressProxyUrl { get; set; }
}

/// <summary>A resolved profile ready to shape a <see cref="SandboxSpec"/>.</summary>
public sealed record SandboxProfile(
    string Name,
    string Runtime,
    SandboxResourceLimits Limits,
    SandboxEgress Egress,
    string? EgressProxyUrl);

/// <summary>
/// Resolves the isolation profile for a session with precedence
/// session-override → workspace-default → global default, clamped to <see cref="SandboxOptions.AllowedProfiles"/>.
/// Single-valued today ("standard"); adding gVisor/Firecracker is a config + ops change, not a redesign.
/// </summary>
public sealed class SandboxProfileResolver(IOptions<SandboxOptions> options)
{
    private readonly SandboxOptions _options = options.Value;

    public SandboxProfile Resolve(string? sessionOverride = null, string? workspaceDefault = null)
    {
        var requested = FirstNonEmpty(sessionOverride, workspaceDefault, _options.DefaultProfile);

        var name = _options.AllowedProfiles.Contains(requested, StringComparer.OrdinalIgnoreCase)
            ? requested
            : _options.DefaultProfile;

        if (!TryGetProfile(name, out var cfg))
        {
            // Missing config must never crash provisioning — fall back to built-in standard/runc.
            cfg = new SandboxProfileConfig();
            name = _options.DefaultProfile;
        }

        var egress = string.Equals(cfg.Egress, "proxy", StringComparison.OrdinalIgnoreCase)
            ? SandboxEgress.Proxy
            : SandboxEgress.Open;

        return new SandboxProfile(
            name,
            cfg.Runtime,
            new SandboxResourceLimits(checked((long)cfg.MemoryMb * 1024 * 1024), cfg.Cpus, cfg.PidsLimit),
            egress,
            cfg.EgressProxyUrl);
    }

    private bool TryGetProfile(string name, out SandboxProfileConfig cfg)
    {
        foreach (var (key, value) in _options.Profiles)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                cfg = value;
                return true;
            }
        }

        cfg = null!;
        return false;
    }

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "standard";
}
