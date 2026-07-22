using k8s.Models;
using Mintokei.Sandbox.Kubernetes;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>Pure-translation tests for the K8s broker objects (no cluster) — the Pod, Service, and the
/// deny-by-default NetworkPolicies that make the broker the sandbox's only route out.</summary>
public class KubernetesBrokerSpecTests
{
    private const string Session = "sess-1";

    [Fact]
    public void Broker_pod_is_non_root_labelled_per_session_and_exposes_the_broker_ports()
    {
        var env = new[] { new KeyValuePair<string, string>("BROKER_ALLOW", "api.anthropic.com") };
        var pod = KubernetesBrokerSpec.BuildBrokerPod(Session, "mintokei/sandbox-broker:1", env);

        Assert.Equal("sess-1-broker", pod.Metadata.Name);
        Assert.Equal("sess-1", pod.Metadata.Labels[KubernetesBrokerSpec.SessionLabel]);
        Assert.Equal(KubernetesBrokerSpec.BrokerRole, pod.Metadata.Labels[KubernetesBrokerSpec.RoleLabel]);
        Assert.Equal("Always", pod.Spec.RestartPolicy);

        var c = Assert.Single(pod.Spec.Containers);
        Assert.Equal("mintokei/sandbox-broker:1", c.Image);
        Assert.Equal("api.anthropic.com", Assert.Single(c.Env, e => e.Name == "BROKER_ALLOW").Value);
        Assert.Contains(c.Ports, p => p.ContainerPort == 3128);
        Assert.Contains(c.Ports, p => p.ContainerPort == 3132);
        Assert.False(c.SecurityContext.AllowPrivilegeEscalation);
        Assert.Contains("ALL", c.SecurityContext.Capabilities.Drop);
        Assert.True(c.SecurityContext.RunAsNonRoot);
        Assert.Equal(10002, c.SecurityContext.RunAsUser);
    }

    [Fact]
    public void Service_selects_the_broker_pod_and_exposes_every_port()
    {
        var svc = KubernetesBrokerSpec.BuildBrokerService(Session);

        Assert.Equal("sess-1-broker", svc.Metadata.Name);         // == Pod name → stable DNS
        Assert.Equal("ClusterIP", svc.Spec.Type);
        Assert.Equal(KubernetesBrokerSpec.BrokerRole, svc.Spec.Selector[KubernetesBrokerSpec.RoleLabel]);
        Assert.Equal("sess-1", svc.Spec.Selector[KubernetesBrokerSpec.SessionLabel]);
        Assert.Equal(KubernetesBrokerSpec.BrokerPorts.Count, svc.Spec.Ports.Count);
        Assert.Contains(svc.Spec.Ports, p => p.Port == 3130);     // a model port
    }

    [Fact]
    public void Sandbox_egress_policy_denies_all_but_the_broker_and_dns()
    {
        var np = KubernetesBrokerSpec.BuildSandboxEgressPolicy(Session);

        Assert.Equal(["Egress"], np.Spec.PolicyTypes);
        Assert.Equal(KubernetesBrokerSpec.SandboxRole, np.Spec.PodSelector.MatchLabels[KubernetesBrokerSpec.RoleLabel]);
        Assert.Equal(2, np.Spec.Egress.Count);

        // Rule 1: to the broker Pod (selected by role=broker) — the sole route out.
        var toBroker = np.Spec.Egress[0];
        Assert.Equal(KubernetesBrokerSpec.BrokerRole,
            Assert.Single(toBroker.To).PodSelector.MatchLabels[KubernetesBrokerSpec.RoleLabel]);

        // Rule 2: DNS only (53) — nothing else leaks.
        var dns = np.Spec.Egress[1];
        Assert.All(dns.Ports, p => Assert.Equal("53", p.Port.Value));
        Assert.Contains(dns.Ports, p => p.Protocol == "UDP");
    }

    [Fact]
    public void Broker_policy_admits_only_its_sandbox_and_keeps_egress_open()
    {
        var np = KubernetesBrokerSpec.BuildBrokerPolicy(Session);

        Assert.Equal(["Ingress", "Egress"], np.Spec.PolicyTypes);
        Assert.Equal(KubernetesBrokerSpec.BrokerRole, np.Spec.PodSelector.MatchLabels[KubernetesBrokerSpec.RoleLabel]);
        // Ingress ONLY from its sandbox.
        Assert.Equal(KubernetesBrokerSpec.SandboxRole,
            Assert.Single(Assert.Single(np.Spec.Ingress).FromProperty).PodSelector.MatchLabels[KubernetesBrokerSpec.RoleLabel]);
        // Egress open (one empty rule) — the CONNECT proxy does the hostname allowlisting.
        var egress = Assert.Single(np.Spec.Egress);
        Assert.True(egress.To is null || egress.To.Count == 0);
    }

    [Fact]
    public void Sandbox_labels_match_what_the_egress_policy_selects()
    {
        var labels = KubernetesBrokerSpec.SandboxLabels(Session);
        var np = KubernetesBrokerSpec.BuildSandboxEgressPolicy(Session);

        foreach (var (k, v) in np.Spec.PodSelector.MatchLabels)
            Assert.Equal(v, labels[k]); // the Pod (built with these labels) is selected by the policy
    }

    [Fact]
    public void Broker_name_is_dns_safe_and_ends_with_broker()
    {
        var name = KubernetesBrokerSpec.BrokerName("Sess_Weird.Name!!");
        Assert.EndsWith("-broker", name);
        Assert.DoesNotContain(name, c => !(char.IsAsciiLetterOrDigit(c) || c == '-'));
        Assert.Equal(name.ToLowerInvariant(), name);
    }
}
