using System.Globalization;
using k8s.Models;
using Mintokei.Sandbox.Docker;

namespace Mintokei.Sandbox.Kubernetes;

/// <summary>
/// Pure translation of a <see cref="SandboxSpec"/> into a Kubernetes <see cref="V1Pod"/> — the
/// containerd/k8s analogue of <see cref="DockerCommand.BuildRunArgs"/>. No I/O, so the manifest is
/// unit-tested without a cluster. One Pod per session (one sandbox = one ephemeral single-slot runner),
/// keeping the isolation runtime a single knob (<c>runtimeClassName</c>) so switching profiles
/// (runc → runsc → kata-fc) never touches this shape.
///
/// Backend differences from Docker, by design:
/// <list type="bullet">
///   <item><b>runc</b> is containerd's default, so RuntimeClass <c>"runc"</c> maps to a null
///     <c>runtimeClassName</c> (node default); a non-default runtime (runsc/kata) names a RuntimeClass
///     that must already exist on the cluster — same single-knob mapping as Docker's <c>--runtime</c>.</item>
///   <item><b>PidsLimit</b> has no per-Pod field (it is a node-level kubelet setting, <c>--pod-max-pids</c>);
///     the profile's PidsLimit is therefore not enforced here. Memory + CPU map to container resource limits.</item>
///   <item><b>AddHostGateway</b> (Docker's dev-only host.docker.internal) is ignored: in-cluster the sandbox
///     runner reaches the control plane via the API's Service DNS, configured in the runner's backend URLs.</item>
///   <item><b>Mounts</b> become <c>hostPath</c> volumes — node-local, correct for a single-node host (creds /
///     repo-cache live on the node); a multi-node cluster would need a different distribution mechanism.</item>
/// </list>
/// </summary>
public static class KubernetesPodSpec
{
    /// <summary>Pod label key applied to every sandbox (value <c>"1"</c>), mirroring the Docker managed
    /// label, so <see cref="KubernetesSandboxRuntime.ListManagedAsync"/> reconciles only Pods we launched.</summary>
    public const string ManagedLabel = DockerCommand.ManagedLabel;

    /// <summary>The single sandbox container's name (the runner). Must be a DNS label.</summary>
    public const string ContainerName = "sandbox";

    public static V1Pod Build(SandboxSpec spec)
    {
        var volumes = new List<V1Volume>();
        var mounts = new List<V1VolumeMount>();

        // tmpfs targets → in-memory emptyDir volumes (Docker's --tmpfs, e.g. the runner data dir /data).
        var t = 0;
        foreach (var target in spec.Tmpfs)
        {
            var name = $"tmpfs-{t++}";
            volumes.Add(new V1Volume { Name = name, EmptyDir = new V1EmptyDirVolumeSource { Medium = "Memory" } });
            mounts.Add(new V1VolumeMount { Name = name, MountPath = target });
        }

        // Host mounts (RO creds/repo-cache) → hostPath volumes (Docker's -v src:target:ro). Node-local.
        var h = 0;
        foreach (var m in spec.Mounts)
        {
            var name = $"host-{h++}";
            volumes.Add(new V1Volume { Name = name, HostPath = new V1HostPathVolumeSource { Path = m.Source } });
            mounts.Add(new V1VolumeMount { Name = name, MountPath = m.Target, ReadOnlyProperty = m.ReadOnly });
        }

        var env = spec.Env.Select(kv => new V1EnvVar { Name = kv.Key, Value = kv.Value }).ToList();

        // Proxy egress: force the container through an allowlisting HTTP CONNECT proxy (Docker's HTTPS_PROXY).
        if (spec.Egress == SandboxEgress.Proxy && !string.IsNullOrWhiteSpace(spec.EgressProxyUrl))
        {
            env.Add(new V1EnvVar { Name = "HTTPS_PROXY", Value = spec.EgressProxyUrl });
            env.Add(new V1EnvVar { Name = "HTTP_PROXY", Value = spec.EgressProxyUrl });
        }

        var container = new V1Container
        {
            Name = ContainerName,
            Image = spec.Image,
            Args = spec.Args.Count > 0 ? spec.Args.ToList() : null, // → container entrypoint (runner flags)
            Env = env.Count > 0 ? env : null,
            VolumeMounts = mounts.Count > 0 ? mounts : null,
            Resources = new V1ResourceRequirements
            {
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    // MemoryBytes as a plain byte count; Cpus as cores (fractional allowed).
                    ["memory"] = new ResourceQuantity(spec.Limits.MemoryBytes.ToString(CultureInfo.InvariantCulture)),
                    ["cpu"] = new ResourceQuantity(spec.Limits.Cpus.ToString(CultureInfo.InvariantCulture)),
                },
            },
            SecurityContext = new V1SecurityContext
            {
                AllowPrivilegeEscalation = false,                             // Docker --security-opt no-new-privileges
                Capabilities = new V1Capabilities { Drop = ["ALL"] },         // Docker --cap-drop ALL
            },
        };

        return new V1Pod
        {
            ApiVersion = "v1",
            Kind = "Pod",
            Metadata = new V1ObjectMeta
            {
                Name = spec.Name,
                Labels = new Dictionary<string, string> { [ManagedLabel] = "1" },
            },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Never",                                      // single-shot ephemeral session
                RuntimeClassName = ResolveRuntimeClassName(spec.RuntimeClass),
                Containers = [container],
                Volumes = volumes.Count > 0 ? volumes : null,
            },
        };
    }

    /// <summary>runc is the node default (empty runtimeClassName); anything else names a cluster RuntimeClass.</summary>
    private static string? ResolveRuntimeClassName(string runtimeClass) =>
        string.IsNullOrWhiteSpace(runtimeClass) || string.Equals(runtimeClass, "runc", StringComparison.OrdinalIgnoreCase)
            ? null
            : runtimeClass;
}
