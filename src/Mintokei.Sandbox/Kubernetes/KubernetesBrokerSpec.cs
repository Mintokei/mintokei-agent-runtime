using k8s.Models;
using Mintokei.Sandbox.Docker;

namespace Mintokei.Sandbox.Kubernetes;

/// <summary>
/// Pure builders for the per-session Kubernetes broker — the K8s analogue of <see cref="RemoteSandboxBroker"/>'s
/// docker <c>--internal</c> network + dual-homed container, expressed as API objects (no I/O, so unit-tested
/// without a cluster). Three pieces enforce the same "the sandbox's only route out is the broker" posture:
/// <list type="number">
///   <item>the <b>broker Pod</b> (the broker image + its <c>BROKER_*</c> env), labelled per session;</item>
///   <item>a <b>Service</b> giving the sandbox a stable DNS name to reach the broker;</item>
///   <item>a <b>deny-by-default egress NetworkPolicy</b> on the sandbox (egress only to the broker + DNS) and a
///     companion policy on the broker (ingress only from its sandbox; egress open, since the broker's CONNECT
///     proxy does the hostname allowlisting — NetworkPolicy can't match hostnames).</item>
/// </list>
/// Requires a NetworkPolicy-enforcing CNI (calico / cilium / k3s's kube-router). Both Pods carry a per-session
/// label so the policies select them; the sandbox Pod must be built with <see cref="SandboxLabels"/>.
/// </summary>
public static class KubernetesBrokerSpec
{
    /// <summary>Per-session label tying a broker + its sandbox together (value = the sanitized session name).</summary>
    public const string SessionLabel = "mintokei.sandbox.session";

    /// <summary>Role within a session: <c>"broker"</c> or <c>"sandbox"</c>, so a policy can select one side.</summary>
    public const string RoleLabel = "mintokei.sandbox.role";

    public const string BrokerRole = "broker";
    public const string SandboxRole = "sandbox";

    // The broker needs no privileges — matches Dockerfile.broker's `useradd -u 10002 broker`.
    private const long BrokerUid = 10002;

    /// <summary>The broker's listening ports (CONNECT proxy, git-credential mint, model providers, GitHub mint).
    /// The Service exposes all of them; the sandbox egress policy allows the sandbox to reach the broker Pod on
    /// any port, so this list only needs to cover what the Service advertises.</summary>
    public static IReadOnlyList<(string Name, int Port)> BrokerPorts { get; } =
    [
        ("proxy", 3128), ("gitmint", 3129), ("anthropic", 3130), ("openai", 3131), ("github", 3132),
    ];

    /// <summary>The canonical per-session key (DNS-1123): the label value + the base of every object name, so
    /// the broker's resources are recoverable from any one of them at teardown.</summary>
    public static string Session(string sessionName) =>
        Dns1123(RemoteSandboxBroker.BrokerContainerName(sessionName)[..^"-broker".Length]);

    /// <summary>DNS-1123 broker object name (Pod + Service share it, so the Service DNS is
    /// <c>&lt;name&gt;.&lt;namespace&gt;.svc</c>).</summary>
    public static string BrokerName(string sessionName) => $"{Session(sessionName)}-broker";

    /// <summary>The labels the SANDBOX Pod must carry so the egress NetworkPolicy selects it.</summary>
    public static IDictionary<string, string> SandboxLabels(string sessionName) => new Dictionary<string, string>
    {
        [KubernetesPodSpec.ManagedLabel] = "1",
        [SessionLabel] = Session(sessionName),
        [RoleLabel] = SandboxRole,
    };

    /// <summary>
    /// The broker Pod: the broker image + its env, non-root, minimal resources, labelled per session.
    /// <paramref name="credentialMounts"/> (optional) are node cred dirs the broker resolves <c>${json:…}</c>/
    /// <c>${gitcreds:…}</c> refs from — but they are <c>0600 root</c> and the broker runs non-root, so (exactly as
    /// <see cref="KubernetesPodSpec"/> does for the sandbox) a ROOT initContainer stages a broker-uid-owned copy
    /// into an in-memory emptyDir the broker reads. The real token is read HERE in the broker Pod, never by the
    /// control plane and never written to a k8s Secret.
    /// </summary>
    public static V1Pod BuildBrokerPod(
        string sessionName, string image, IReadOnlyList<KeyValuePair<string, string>> env,
        IReadOnlyList<SandboxBrokerCredentialMount>? credentialMounts = null, string? imagePullPolicy = null)
    {
        var session = Session(sessionName);

        var volumes = new List<V1Volume>();
        var brokerMounts = new List<V1VolumeMount>();
        var initContainers = new List<V1Container>();

        var mounts = credentialMounts ?? [];
        if (mounts.Count > 0)
        {
            var initMounts = new List<V1VolumeMount>();
            var cps = new List<string>();
            for (var i = 0; i < mounts.Count; i++)
            {
                var m = mounts[i];
                var srcVol = $"bcred-src-{i}";
                var stagedVol = $"bcred-{i}";
                volumes.Add(new V1Volume { Name = srcVol, HostPath = new V1HostPathVolumeSource { Path = m.HostDir } });
                volumes.Add(new V1Volume { Name = stagedVol, EmptyDir = new V1EmptyDirVolumeSource { Medium = "Memory" } });
                initMounts.Add(new V1VolumeMount { Name = srcVol, MountPath = $"/stage-in/{i}", ReadOnlyProperty = true });
                initMounts.Add(new V1VolumeMount { Name = stagedVol, MountPath = $"/stage-out/{i}" });
                // The broker reads the STAGED copy at the ref path (e.g. /creds/claude) — not the raw hostPath.
                brokerMounts.Add(new V1VolumeMount { Name = stagedVol, MountPath = m.ContainerDir, ReadOnlyProperty = true });
                cps.Add($"cp -aL /stage-in/{i}/. /stage-out/{i}/ 2>/dev/null || true");
            }
            var script = string.Join("; ", cps) + $"; chown -R {BrokerUid}:{BrokerUid} /stage-out";
            initContainers.Add(new V1Container
            {
                Name = "stage-creds",
                Image = image, // reuse the broker image (already scheduled) — no extra pull
                ImagePullPolicy = imagePullPolicy,
                Command = ["sh", "-c", script],
                VolumeMounts = initMounts,
                SecurityContext = new V1SecurityContext
                {
                    RunAsNonRoot = false,
                    RunAsUser = 0,       // root: the only thing that can read the 0600 root-owned node creds
                    RunAsGroup = 0,
                    AllowPrivilegeEscalation = false,
                    Capabilities = new V1Capabilities { Drop = ["ALL"], Add = ["CHOWN", "DAC_OVERRIDE", "FOWNER"] },
                },
            });
        }

        return new V1Pod
        {
            ApiVersion = "v1",
            Kind = "Pod",
            Metadata = new V1ObjectMeta
            {
                Name = BrokerName(sessionName),
                Labels = new Dictionary<string, string>
                {
                    [KubernetesPodSpec.ManagedLabel] = "1",
                    [SessionLabel] = session,
                    [RoleLabel] = BrokerRole,
                },
            },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Always", // stays up for the whole session; torn down explicitly on stop
                InitContainers = initContainers.Count > 0 ? initContainers : null,
                Containers =
                [
                    new V1Container
                    {
                        Name = "broker",
                        Image = image,
                        ImagePullPolicy = imagePullPolicy, // null → kubelet default; set "IfNotPresent" for a node-imported image
                        Env = env.Count > 0 ? env.Select(kv => new V1EnvVar { Name = kv.Key, Value = kv.Value }).ToList() : null,
                        Ports = BrokerPorts.Select(p => new V1ContainerPort { Name = p.Name, ContainerPort = p.Port }).ToList(),
                        VolumeMounts = brokerMounts.Count > 0 ? brokerMounts : null,
                        Resources = new V1ResourceRequirements
                        {
                            Limits = new Dictionary<string, ResourceQuantity>
                            {
                                ["memory"] = new ResourceQuantity("256Mi"),
                                ["cpu"] = new ResourceQuantity("0.5"),
                            },
                        },
                        SecurityContext = new V1SecurityContext
                        {
                            AllowPrivilegeEscalation = false,
                            Capabilities = new V1Capabilities { Drop = ["ALL"] },
                            RunAsNonRoot = true,
                            RunAsUser = BrokerUid,
                            RunAsGroup = BrokerUid,
                        },
                    },
                ],
                Volumes = volumes.Count > 0 ? volumes : null,
            },
        };
    }

    /// <summary>A ClusterIP Service the sandbox reaches the broker by (stable DNS across the broker Pod's life).</summary>
    public static V1Service BuildBrokerService(string sessionName)
    {
        var session = Session(sessionName);
        return new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = new V1ObjectMeta
            {
                Name = BrokerName(sessionName),
                Labels = new Dictionary<string, string> { [KubernetesPodSpec.ManagedLabel] = "1", [SessionLabel] = session },
            },
            Spec = new V1ServiceSpec
            {
                Type = "ClusterIP",
                Selector = new Dictionary<string, string> { [SessionLabel] = session, [RoleLabel] = BrokerRole },
                Ports = BrokerPorts.Select(p => new V1ServicePort
                {
                    Name = p.Name,
                    Port = p.Port,
                    TargetPort = p.Port,
                    Protocol = "TCP",
                }).ToList(),
            },
        };
    }

    /// <summary>Deny-by-default EGRESS for the sandbox: it may reach ONLY the broker Pod (any port) and DNS —
    /// everything else is dropped, so the broker is its sole route out.</summary>
    public static V1NetworkPolicy BuildSandboxEgressPolicy(string sessionName)
    {
        var session = Session(sessionName);
        return new V1NetworkPolicy
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = new V1ObjectMeta
            {
                Name = Dns1123($"{session}-sandbox-egress"),
                Labels = new Dictionary<string, string> { [KubernetesPodSpec.ManagedLabel] = "1", [SessionLabel] = session },
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string> { [SessionLabel] = session, [RoleLabel] = SandboxRole },
                },
                PolicyTypes = ["Egress"],
                Egress =
                [
                    // → the broker Pod (any port): its sole route out.
                    new V1NetworkPolicyEgressRule
                    {
                        To =
                        [
                            new V1NetworkPolicyPeer
                            {
                                PodSelector = new V1LabelSelector
                                {
                                    MatchLabels = new Dictionary<string, string> { [SessionLabel] = session, [RoleLabel] = BrokerRole },
                                },
                            },
                        ],
                    },
                    // → DNS, so it can resolve the broker Service name (and nothing else leaks — 53 only).
                    new V1NetworkPolicyEgressRule
                    {
                        Ports = [new V1NetworkPolicyPort { Protocol = "UDP", Port = 53 }, new V1NetworkPolicyPort { Protocol = "TCP", Port = 53 }],
                    },
                ],
            },
        };
    }

    /// <summary>Scopes the broker Pod: ingress ONLY from its sandbox, egress open (its CONNECT proxy does the
    /// hostname allowlisting — NetworkPolicy can't match hostnames). Makes containment self-contained regardless
    /// of the namespace's default policies.</summary>
    public static V1NetworkPolicy BuildBrokerPolicy(string sessionName)
    {
        var session = Session(sessionName);
        return new V1NetworkPolicy
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = new V1ObjectMeta
            {
                Name = Dns1123($"{session}-broker-policy"),
                Labels = new Dictionary<string, string> { [KubernetesPodSpec.ManagedLabel] = "1", [SessionLabel] = session },
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string> { [SessionLabel] = session, [RoleLabel] = BrokerRole },
                },
                PolicyTypes = ["Ingress", "Egress"],
                Ingress =
                [
                    new V1NetworkPolicyIngressRule
                    {
                        FromProperty =
                        [
                            new V1NetworkPolicyPeer
                            {
                                PodSelector = new V1LabelSelector
                                {
                                    MatchLabels = new Dictionary<string, string> { [SessionLabel] = session, [RoleLabel] = SandboxRole },
                                },
                            },
                        ],
                    },
                ],
                Egress = [new V1NetworkPolicyEgressRule()], // allow all egress (hostname filtering is the proxy's job)
            },
        };
    }

    private static string Dns1123(string s)
    {
        var lowered = new string(s.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
        lowered = lowered.Trim('-');
        if (lowered.Length == 0) lowered = "session";
        return lowered.Length > 63 ? lowered[..63].TrimEnd('-') : lowered;
    }
}
