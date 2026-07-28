using k8s.Models;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Kubernetes;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// Pure-translation tests for the k8s backend (no cluster) — the analogue of <c>DockerCommandTests</c>.
/// </summary>
public class KubernetesPodSpecTests
{
    private static SandboxSpec Spec() => new()
    {
        Image = "mintokei/sandbox:latest",
        Name = "sess-1",
        RuntimeClass = "runc",
        Limits = new SandboxResources(4L * 1024 * 1024 * 1024, 2, 512),
        Mounts = [new SandboxMount("/repo-cache", "/repo-cache", ReadOnly: true)],
        Env = new Dictionary<string, string> { ["SANDBOX_REPO_URL"] = "https://x/y.git" },
        Args = ["--backend", "https://api", "--token", "tok", "--name", "sess-1"],
    };

    private static V1Container Container(V1Pod pod) => Assert.Single(pod.Spec.Containers);

    [Fact]
    public void Sets_pod_name_managed_label_and_single_shot_restart_policy()
    {
        var pod = KubernetesPodSpec.Build(Spec());

        Assert.Equal("sess-1", pod.Metadata.Name);
        Assert.Equal("1", pod.Metadata.Labels[KubernetesPodSpec.ManagedLabel]);
        Assert.Equal("Never", pod.Spec.RestartPolicy);
    }

    [Fact]
    public void Broker_egress_without_wiring_fails_closed()
    {
        // A raw Broker spec (no proxy = the runtime hasn't started the broker + NetworkPolicy) must be refused.
        var spec = Spec() with { Egress = SandboxEgress.Broker, EgressAllowlist = ["github.com"] };
        var ex = Assert.Throws<SandboxRuntimeException>(() => KubernetesPodSpec.Build(spec));
        Assert.Contains("fail-closed", ex.Message);
    }

    [Fact]
    public void Broker_egress_when_wired_builds_the_pod_with_proxy_env_and_session_labels()
    {
        var spec = Spec() with
        {
            Egress = SandboxEgress.Broker,
            EgressAllowlist = ["github.com"],
            EgressProxyUrl = "http://sess-1-broker:3128", // set by the runtime after starting the broker
        };
        var pod = KubernetesPodSpec.Build(spec);

        var c = Container(pod);
        Assert.Equal("http://sess-1-broker:3128", Assert.Single(c.Env, e => e.Name == "HTTPS_PROXY").Value);
        // carries the per-session labels the broker's NetworkPolicy selects it by
        Assert.Equal(KubernetesBrokerSpec.SandboxRole, pod.Metadata.Labels[KubernetesBrokerSpec.RoleLabel]);
        Assert.Equal("1", pod.Metadata.Labels[KubernetesPodSpec.ManagedLabel]);
    }

    [Fact]
    public void Container_carries_image_and_runner_flag_args()
    {
        var c = Container(KubernetesPodSpec.Build(Spec()));

        Assert.Equal("mintokei/sandbox:latest", c.Image);
        Assert.Equal(["--backend", "https://api", "--token", "tok", "--name", "sess-1"], c.Args);
    }

    [Fact]
    public void Maps_resource_limits_and_requests_burstable()
    {
        var c = Container(KubernetesPodSpec.Build(Spec())); // Limits = 4Gi / 2 cpu

        // Limit = the hard ceiling (burst cap).
        Assert.Equal(new ResourceQuantity("4294967296"), c.Resources.Limits["memory"]);
        Assert.Equal(new ResourceQuantity("2"), c.Resources.Limits["cpu"]);

        // Request = burstable reservation (~¼ CPU / ½ memory of the limit) so many sessions fit on the node
        // instead of each reserving the full limit (Guaranteed → "Insufficient cpu" FailedScheduling).
        Assert.Equal(new ResourceQuantity("1073741824"), c.Resources.Requests["memory"]); // min(4Gi/2, 1Gi) = 1Gi
        Assert.Equal(new ResourceQuantity("0.5"), c.Resources.Requests["cpu"]);            // 2 * 0.25
        Assert.True(c.Resources.Requests["cpu"].ToDouble() < c.Resources.Limits["cpu"].ToDouble()); // burstable
    }

    [Fact]
    public void Applies_standard_hardening()
    {
        var pod = KubernetesPodSpec.Build(Spec());
        var c = Container(pod);

        Assert.False(c.SecurityContext.AllowPrivilegeEscalation);           // no-new-privileges
        Assert.Contains("ALL", c.SecurityContext.Capabilities.Drop);        // cap-drop ALL

        // Non-root: refuse to run as root, run as the image's agent uid, and fsGroup so /data is writable.
        Assert.True(c.SecurityContext.RunAsNonRoot);
        Assert.Equal(SandboxImage.AgentUid, c.SecurityContext.RunAsUser);
        Assert.Equal(SandboxImage.AgentUid, pod.Spec.SecurityContext.FsGroup);
    }

    [Fact]
    public void Maps_env_and_host_mounts()
    {
        var pod = KubernetesPodSpec.Build(Spec());
        var c = Container(pod);

        Assert.Contains(c.Env, e => e.Name == "SANDBOX_REPO_URL" && e.Value == "https://x/y.git");

        // Host mount → RO hostPath volume + volumeMount.
        Assert.Contains(pod.Spec.Volumes, v => v.HostPath?.Path == "/repo-cache");
        Assert.Contains(c.VolumeMounts, m => m.MountPath == "/repo-cache" && m.ReadOnlyProperty == true);
    }

    private static SandboxSpec SpecWithSeed() => Spec() with
    {
        Mounts =
        [
            new SandboxMount("/repo-cache", "/repo-cache", ReadOnly: true),
            new SandboxMount("/root/.claude", "/seed/.claude", ReadOnly: true),
            new SandboxMount("/root/.claude.json", "/seed/.claude.json", ReadOnly: true),
            new SandboxMount("/root/sandbox-git-creds", "/seed/git", ReadOnly: true),
        ],
    };

    [Fact]
    public void Seed_creds_are_staged_by_a_root_initContainer_the_agent_then_reads_from_an_emptydir()
    {
        var pod = KubernetesPodSpec.Build(SpecWithSeed(), "Never");
        var main = Container(pod);

        // A ROOT initContainer stages the creds — the only thing that can read the 0600 root-owned node files.
        var init = Assert.Single(pod.Spec.InitContainers);
        Assert.Equal(0, init.SecurityContext.RunAsUser);
        Assert.False(init.SecurityContext.RunAsNonRoot);
        Assert.Contains("CHOWN", init.SecurityContext.Capabilities.Add);
        Assert.Equal("Never", init.ImagePullPolicy); // reuses the sandbox image (already on the node)

        // It mounts each cred SOURCE under /stage-in and the shared emptyDir at /stage-out, then chowns to the agent uid.
        Assert.Contains(init.VolumeMounts, m => m.MountPath == "/stage-in/.claude" && m.ReadOnlyProperty == true);
        Assert.Contains(init.VolumeMounts, m => m.MountPath == "/stage-in/.claude.json");
        Assert.Contains(init.VolumeMounts, m => m.MountPath == "/stage-in/git");
        Assert.Contains(init.VolumeMounts, m => m.MountPath == "/stage-out");
        Assert.Contains($"chown -R {SandboxImage.AgentUid}:{SandboxImage.AgentUid} /stage-out", init.Command[^1]);

        // The AGENT reads the staged copy at /seed from an in-memory emptyDir — never a raw hostPath of the creds.
        var seed = Assert.Single(main.VolumeMounts, m => m.MountPath == "/seed");
        Assert.Contains(pod.Spec.Volumes, v => v.Name == seed.Name && v.EmptyDir?.Medium == "Memory");
        Assert.DoesNotContain(main.VolumeMounts, m => m.MountPath.StartsWith("/seed/", StringComparison.Ordinal));
        // The cred hostPaths exist (for the init) but are NOT mounted into the main container.
        var credVolNames = pod.Spec.Volumes.Where(v => v.HostPath?.Path is "/root/.claude" or "/root/sandbox-git-creds").Select(v => v.Name).ToHashSet();
        Assert.DoesNotContain(main.VolumeMounts, m => credVolNames.Contains(m.Name));

        // A non-cred mount (repo cache) is still a direct RO hostPath on the main container.
        Assert.Contains(main.VolumeMounts, m => m.MountPath == "/repo-cache");
        Assert.Contains(pod.Spec.Volumes, v => v.HostPath?.Path == "/repo-cache");
    }

    [Fact]
    public void No_seed_mounts_means_no_init_container()
        => Assert.Null(KubernetesPodSpec.Build(Spec()).Spec.InitContainers); // only /repo-cache, no /seed/*

    [Fact]
    public void Tmpfs_targets_become_in_memory_emptydir_volumes()
    {
        var pod = KubernetesPodSpec.Build(Spec()); // default Tmpfs = ["/data"]
        var c = Container(pod);

        Assert.Contains(pod.Spec.Volumes, v => v.EmptyDir?.Medium == "Memory");
        Assert.Contains(c.VolumeMounts, m => m.MountPath == "/data");
    }

    [Fact]
    public void ReadOnlyRootFilesystem_is_unset_by_default()
        => Assert.Null(Container(KubernetesPodSpec.Build(Spec())).SecurityContext.ReadOnlyRootFilesystem);

    [Fact]
    public void ReadOnlyRootfs_sets_readonly_root_filesystem()
        => Assert.True(Container(KubernetesPodSpec.Build(Spec() with { ReadOnlyRootfs = true }))
            .SecurityContext.ReadOnlyRootFilesystem);

    [Fact]
    public void Tmpfs_defers_to_a_real_mount_at_the_same_path()
    {
        // /repos requested as tmpfs AND mounted (the persisted volume) → exactly one volumeMount at /repos,
        // and it's the mount — never a second emptyDir at the same path (which would be an invalid Pod).
        var pod = KubernetesPodSpec.Build(Spec() with
        {
            Tmpfs = ["/data", "/repos"],
            Mounts = [new SandboxMount("/host/repos", "/repos", ReadOnly: false)],
        });
        var c = Container(pod);

        var repos = Assert.Single(c.VolumeMounts, m => m.MountPath == "/repos");
        Assert.Contains(pod.Spec.Volumes, v => v.Name == repos.Name && v.HostPath?.Path == "/host/repos");
        Assert.Contains(c.VolumeMounts, m => m.MountPath == "/data"); // /data still an emptyDir tmpfs
    }

    [Fact]
    public void Runc_maps_to_node_default_runtime_class()
    {
        var pod = KubernetesPodSpec.Build(Spec() with { RuntimeClass = "runc" });
        Assert.Null(pod.Spec.RuntimeClassName);
    }

    [Fact]
    public void Non_default_runtime_names_a_runtime_class()
    {
        var pod = KubernetesPodSpec.Build(Spec() with { RuntimeClass = "runsc" });
        Assert.Equal("runsc", pod.Spec.RuntimeClassName);
    }

    [Fact]
    public void Proxy_egress_injects_proxy_env()
    {
        var c = Container(KubernetesPodSpec.Build(Spec() with
        {
            Egress = SandboxEgress.Proxy,
            EgressProxyUrl = "http://proxy:3128",
        }));

        Assert.Contains(c.Env, e => e.Name == "HTTPS_PROXY" && e.Value == "http://proxy:3128");
        Assert.Contains(c.Env, e => e.Name == "HTTP_PROXY" && e.Value == "http://proxy:3128");
    }

    [Fact]
    public void Image_pull_policy_is_unset_by_default()
    {
        // Null → kubelet default (Always for :latest, else IfNotPresent).
        Assert.Null(Container(KubernetesPodSpec.Build(Spec())).ImagePullPolicy);
    }

    [Fact]
    public void Image_pull_policy_is_applied_when_configured()
    {
        // "Never" is how a node-imported private image avoids a failing registry pull.
        Assert.Equal("Never", Container(KubernetesPodSpec.Build(Spec(), "Never")).ImagePullPolicy);
    }

    [Fact]
    public void AddHostGateway_is_ignored_by_the_k8s_backend()
    {
        // Docker-only dev knob (host.docker.internal); k8s reaches the API via Service DNS, so no host aliases.
        var pod = KubernetesPodSpec.Build(Spec() with { AddHostGateway = true });
        Assert.Null(pod.Spec.HostAliases);
    }
}
