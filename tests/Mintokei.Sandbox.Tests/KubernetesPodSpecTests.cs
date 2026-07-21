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
        Limits = new SandboxResourceLimits(4L * 1024 * 1024 * 1024, 2, 512),
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
    public void Container_carries_image_and_runner_flag_args()
    {
        var c = Container(KubernetesPodSpec.Build(Spec()));

        Assert.Equal("mintokei/sandbox:latest", c.Image);
        Assert.Equal(["--backend", "https://api", "--token", "tok", "--name", "sess-1"], c.Args);
    }

    [Fact]
    public void Maps_resource_limits()
    {
        var c = Container(KubernetesPodSpec.Build(Spec()));

        Assert.Equal(new ResourceQuantity("4294967296"), c.Resources.Limits["memory"]);
        Assert.Equal(new ResourceQuantity("2"), c.Resources.Limits["cpu"]);
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

    [Fact]
    public void Tmpfs_targets_become_in_memory_emptydir_volumes()
    {
        var pod = KubernetesPodSpec.Build(Spec()); // default Tmpfs = ["/data"]
        var c = Container(pod);

        Assert.Contains(pod.Spec.Volumes, v => v.EmptyDir?.Medium == "Memory");
        Assert.Contains(c.VolumeMounts, m => m.MountPath == "/data");
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
