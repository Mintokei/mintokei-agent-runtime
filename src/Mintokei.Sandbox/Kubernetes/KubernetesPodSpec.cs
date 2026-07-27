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
///     repo-cache live on the node); a multi-node cluster would need a different distribution mechanism. The
///     credential <c>/seed/*</c> mounts are the exception: they are <c>0600 root</c>-owned and a non-root pod
///     can't read a raw hostPath of them, so a root <b>initContainer</b> stages a uid-owned copy into an
///     in-memory <c>emptyDir</c> the agent reads (the in-pod analogue of the nested path's credential stager).</item>
/// </list>
/// </summary>
public static class KubernetesPodSpec
{
    /// <summary>Pod label key applied to every sandbox (value <c>"1"</c>), mirroring the Docker managed
    /// label, so <see cref="KubernetesSandboxRuntime.ListManagedAsync"/> reconciles only Pods we launched.</summary>
    public const string ManagedLabel = DockerCommand.ManagedLabel;

    /// <summary>The single sandbox container's name (the runner). Must be a DNS label.</summary>
    public const string ContainerName = "sandbox";

    /// <param name="spec">The backend-agnostic sandbox spec to translate into a Pod.</param>
    /// <param name="imagePullPolicy">"Always" | "IfNotPresent" | "Never", or null for the Kubernetes
    /// default. Set "Never" when the sandbox image is node-imported (see <c>SandboxOptions</c>).</param>
    public static V1Pod Build(SandboxSpec spec, string? imagePullPolicy = null)
    {
        // Fail closed unless broker egress has actually been WIRED: the orchestrator
        // (KubernetesSandboxRuntime) must have started the broker + created the deny-by-default NetworkPolicy and
        // then set EgressProxyUrl (via SandboxBrokerWiring). A raw Broker spec with no proxy would launch a Pod
        // that looks brokered but has open egress and no creds — refuse it (mirrors DockerCommand's NetworkName
        // invariant). The NetworkPolicy that enforces containment is applied by the runtime, not by this Pod.
        if (spec.Egress == SandboxEgress.Broker && string.IsNullOrWhiteSpace(spec.EgressProxyUrl))
            throw new SandboxRuntimeException(
                "broker egress is configured but not wired (no broker proxy) — refusing to launch (fail-closed).");

        var volumes = new List<V1Volume>();
        var mounts = new List<V1VolumeMount>();

        // A real mount (e.g. the persisted /repos volume) already makes its path writable, and two volumeMounts
        // at one path is an invalid Pod — so tmpfs de-dupes against mount targets, mirroring the Docker backend.
        var mountTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in spec.Mounts)
            mountTargets.Add(m.Target);

        // tmpfs targets → in-memory emptyDir volumes (Docker's --tmpfs, e.g. the runner data dir /data).
        var t = 0;
        foreach (var target in spec.Tmpfs)
        {
            if (mountTargets.Contains(target))
                continue;
            var name = $"tmpfs-{t++}";
            volumes.Add(new V1Volume { Name = name, EmptyDir = new V1EmptyDirVolumeSource { Medium = "Memory" } });
            mounts.Add(new V1VolumeMount { Name = name, MountPath = target });
        }

        // Host mounts split by kind:
        //   * cred "/seed/*" mounts are 0600 ROOT-owned node creds — a non-root pod (uid AgentUid) cannot read a
        //     raw hostPath of them (hostPath gets no fsGroup remap), so a ROOT initContainer stages a uid-owned
        //     copy into an in-memory emptyDir the agent reads. This is the in-pod analogue of the nested path's
        //     SandboxCredentialStager (root copies + chowns per session); the node creds stay 0600 and the staged
        //     copy dies with the Pod.
        //   * everything else (repo cache, the persisted /repos volume) → a direct RO hostPath, as before.
        const string SeedRoot = "/seed";
        static bool IsSeed(SandboxMount m) =>
            m.Target == SeedRoot || m.Target.StartsWith(SeedRoot + "/", StringComparison.Ordinal);

        var h = 0;
        foreach (var m in spec.Mounts)
        {
            if (IsSeed(m)) continue; // staged below, not mounted directly
            var name = $"host-{h++}";
            volumes.Add(new V1Volume { Name = name, HostPath = new V1HostPathVolumeSource { Path = m.Source } });
            mounts.Add(new V1VolumeMount { Name = name, MountPath = m.Target, ReadOnlyProperty = m.ReadOnly });
        }

        // Persistent workspace: back /repos with a per-task PVC so the working tree AND the agent-CLI transcript
        // (symlinked onto /repos by the entrypoint) survive a Pod recycle — the difference between "re-provisions"
        // and "actually resumes". The PVC itself is ensured by KubernetesSandboxRuntime BEFORE the Pod is created.
        if (spec.PersistentWorkspaceTaskId is { } wsTask)
        {
            const string wsVol = "workspace";
            volumes.Add(new V1Volume
            {
                Name = wsVol,
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = SandboxWorkspaceStore.Name(wsTask) },
            });
            mounts.Add(new V1VolumeMount { Name = wsVol, MountPath = SandboxSpecFactory.RepoRoot });
        }

        var initContainers = new List<V1Container>();
        var seedMounts = spec.Mounts.Where(IsSeed).ToList();
        if (seedMounts.Count > 0)
        {
            const string staged = "seed-staged";
            volumes.Add(new V1Volume { Name = staged, EmptyDir = new V1EmptyDirVolumeSource { Medium = "Memory" } });

            // The init reads each source (as root) at /stage-in/<rel-under-/seed> and writes the emptyDir at /stage-out.
            var initMounts = new List<V1VolumeMount>();
            foreach (var m in seedMounts)
            {
                var name = $"seed-src-{h++}";
                var rel = m.Target[SeedRoot.Length..].TrimStart('/'); // .claude | .claude.json | .codex | git
                volumes.Add(new V1Volume { Name = name, HostPath = new V1HostPathVolumeSource { Path = m.Source } });
                initMounts.Add(new V1VolumeMount { Name = name, MountPath = $"/stage-in/{rel}", ReadOnlyProperty = true });
            }
            initMounts.Add(new V1VolumeMount { Name = staged, MountPath = "/stage-out" });

            // The agent reads the staged, uid-owned copy at /seed — NOT the unreadable raw hostPath.
            mounts.Add(new V1VolumeMount { Name = staged, MountPath = SeedRoot });

            var uid = SandboxImage.AgentUid.ToString(CultureInfo.InvariantCulture);
            initContainers.Add(new V1Container
            {
                Name = "stage-creds",
                Image = spec.Image,                 // reuse the sandbox image (already on the node) — no extra pull
                ImagePullPolicy = imagePullPolicy,
                // Copy the mounted creds into the emptyDir preserving structure, then hand ownership to the agent
                // uid so the non-root main container can read them. Best-effort per source (missing → skipped).
                Command = ["sh", "-c", $"cp -aL /stage-in/. /stage-out/ 2>/dev/null || true; chown -R {uid}:{uid} /stage-out"],
                VolumeMounts = initMounts,
                SecurityContext = new V1SecurityContext
                {
                    RunAsNonRoot = false,
                    RunAsUser = 0,                  // root: the only thing that can read the 0600 root-owned creds
                    RunAsGroup = 0,
                    AllowPrivilegeEscalation = false,
                    Capabilities = new V1Capabilities { Drop = ["ALL"], Add = ["CHOWN", "DAC_OVERRIDE", "FOWNER"] },
                },
            });
        }

        var env = spec.Env.Select(kv => new V1EnvVar { Name = kv.Key, Value = kv.Value }).ToList();

        // Proxy / Broker egress: force the container through the allowlisting HTTP CONNECT proxy (Docker's
        // HTTPS_PROXY). In Broker mode this is the session broker's proxy; NetworkPolicy makes it the only exit.
        if (spec.Egress is SandboxEgress.Proxy or SandboxEgress.Broker && !string.IsNullOrWhiteSpace(spec.EgressProxyUrl))
        {
            env.Add(new V1EnvVar { Name = "HTTPS_PROXY", Value = spec.EgressProxyUrl });
            env.Add(new V1EnvVar { Name = "HTTP_PROXY", Value = spec.EgressProxyUrl });
        }

        var container = new V1Container
        {
            Name = ContainerName,
            Image = spec.Image,
            ImagePullPolicy = imagePullPolicy, // null → kubelet default (Always for :latest, else IfNotPresent)
            Args = spec.Args.Count > 0 ? spec.Args.ToList() : null, // → container entrypoint (runner flags)
            Env = env.Count > 0 ? env : null,
            VolumeMounts = mounts.Count > 0 ? mounts : null,
            Resources = new V1ResourceRequirements
            {
                // MemoryBytes as a plain byte count; Cpus as cores (fractional allowed). The LIMIT is the hard
                // ceiling (burst cap).
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["memory"] = new ResourceQuantity(spec.Limits.MemoryBytes.ToString(CultureInfo.InvariantCulture)),
                    ["cpu"] = new ResourceQuantity(spec.Limits.Cpus.ToString(CultureInfo.InvariantCulture)),
                },
                // Burstable QoS: a sandbox is a bursty, mostly-I/O agent CLI, so RESERVE far less than the ceiling
                // (~¼ CPU / ½ memory of the limit). Reserving the full limit (Guaranteed) let only ~2 sessions fit
                // on the node before "Insufficient cpu" FailedScheduling — the pod would sit Pending and never come
                // online. CPU is compressible (a low request only throttles under contention, never OOM-kills);
                // memory keeps a generous floor to stay safe under node memory pressure. The limit still caps burst.
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    // Memory: half the limit but capped at 1 GiB — enough working set for an agent CLI + repo, so
                    // the admission cap's worth of sessions fits the node's memory; the pod can still burst to the
                    // full limit (node has headroom). CPU: a quarter of the limit (compressible → safe low reserve).
                    ["memory"] = new ResourceQuantity(
                        Math.Max(256L * 1024 * 1024, Math.Min(spec.Limits.MemoryBytes / 2, 1024L * 1024 * 1024))
                            .ToString(CultureInfo.InvariantCulture)),
                    ["cpu"] = new ResourceQuantity(
                        Math.Max(0.25, spec.Limits.Cpus * 0.25).ToString("0.###", CultureInfo.InvariantCulture)),
                },
            },
            SecurityContext = new V1SecurityContext
            {
                AllowPrivilegeEscalation = false,                             // Docker --security-opt no-new-privileges
                Capabilities = new V1Capabilities { Drop = ["ALL"] },         // Docker --cap-drop ALL
                RunAsNonRoot = true,                                          // refuse to start if the image would run as root
                RunAsUser = SandboxImage.AgentUid,                            // matches the image's USER agent
                RunAsGroup = SandboxImage.AgentUid,
                ReadOnlyRootFilesystem = spec.ReadOnlyRootfs ? true : null,   // Docker --read-only (opt-in per profile)
            },
        };

        return new V1Pod
        {
            ApiVersion = "v1",
            Kind = "Pod",
            Metadata = new V1ObjectMeta
            {
                Name = spec.Name,
                // In broker mode the Pod carries the per-session labels the broker's NetworkPolicy selects it by.
                Labels = spec.Egress == SandboxEgress.Broker
                    ? new Dictionary<string, string>(KubernetesBrokerSpec.SandboxLabels(spec.Name))
                    : new Dictionary<string, string> { [ManagedLabel] = "1" },
            },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Never",                                      // single-shot ephemeral session
                RuntimeClassName = ResolveRuntimeClassName(spec.RuntimeClass),
                // FsGroup makes the in-memory /data emptyDir group-owned + writable by the non-root agent
                // (the kubelet does not chown emptyDir to the container uid on its own).
                SecurityContext = new V1PodSecurityContext { FsGroup = SandboxImage.AgentUid },
                InitContainers = initContainers.Count > 0 ? initContainers : null,
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
