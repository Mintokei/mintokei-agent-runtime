using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Kubernetes;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>The K8s runtime's broker orchestration: start the broker → wire the spec → create the Pod, and
/// fail closed when no broker is registered.</summary>
public class KubernetesSandboxRuntimeBrokerTests
{
    private sealed class FakeBroker : ISandboxBroker
    {
        public int Started, Stopped;
        // ProxyUrl set → the wired spec passes KubernetesPodSpec's "broker must be wired" guard.
        public BrokerEndpoint Endpoint { get; set; } =
            new("", "sbx-1-broker", "http://sbx-1-broker:3128", "http://sbx-1-broker:3129/git-credential");
        public Task<BrokerEndpoint> StartAsync(Guid w, SandboxBrokerRequest r, CancellationToken ct = default)
        { Started++; return Task.FromResult(Endpoint); }
        public Task StopAsync(Guid w, BrokerEndpoint e, CancellationToken ct = default)
        { Stopped++; return Task.CompletedTask; }
    }

    private static SandboxSpec BrokerSpec() => new()
    {
        Image = "img:1",
        Name = "sbx-1",
        RuntimeClass = "runc",
        Limits = new SandboxResourceLimits(1L * 1024 * 1024 * 1024, 1, 128),
        Egress = SandboxEgress.Broker,
        EgressAllowlist = ["api.anthropic.com"],
    };

    private static KubernetesSandboxRuntime Runtime(FakeKubeApi api, ISandboxBroker? broker) =>
        new(api.Client(), Options.Create(new SandboxOptions { KubernetesNamespace = "mk" }),
            NullLogger<KubernetesSandboxRuntime>.Instance, broker);

    [Fact]
    public async Task Provision_in_broker_mode_starts_the_broker_then_creates_the_pod()
    {
        using var api = new FakeKubeApi();
        var broker = new FakeBroker();

        await Runtime(api, broker).ProvisionAsync(BrokerSpec());

        Assert.Equal(1, broker.Started);                                            // broker up first
        Assert.Contains(api.Calls, c => c is ("POST", "/api/v1/namespaces/mk/pods")); // then the sandbox Pod
    }

    [Fact]
    public async Task Provision_in_broker_mode_without_a_registered_broker_fails_closed()
    {
        using var api = new FakeKubeApi();

        var ex = await Assert.ThrowsAsync<SandboxRuntimeException>(
            () => Runtime(api, broker: null).ProvisionAsync(BrokerSpec()));
        Assert.Contains("fail-closed", ex.Message);
    }

    [Fact]
    public async Task Stop_tears_down_the_session_broker_too()
    {
        using var api = new FakeKubeApi();
        var broker = new FakeBroker();

        await Runtime(api, broker).StopAsync(new SandboxHandle("u", "sbx-1", "kubernetes"));

        Assert.Equal(1, broker.Stopped); // best-effort broker teardown keyed off the pod name
    }
}
