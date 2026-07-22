using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Kubernetes;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>Drives <see cref="KubernetesSandboxBroker"/> against a fake in-process API server (see
/// <see cref="FakeKubeApi"/>), verifying the create/delete flow + the returned Service URLs without a cluster.</summary>
public class KubernetesSandboxBrokerTests
{
    private static KubernetesSandboxBroker Broker(FakeKubeApi api) =>
        new(api.Client(), Options.Create(new SandboxOptions { BrokerImage = "brk:1", KubernetesNamespace = "mk" }),
            NullLogger<KubernetesSandboxBroker>.Instance);

    [Fact]
    public async Task StartAsync_creates_pod_service_and_two_network_policies_and_returns_service_urls()
    {
        using var api = new FakeKubeApi();

        var e = await Broker(api).StartAsync(Guid.Empty, new SandboxBrokerRequest(
            "sbx-1", ["api.anthropic.com"],
            new SandboxBrokerSecrets(ModelUpstream: "https://api.anthropic.com", ModelAuth: "x-api-key=sk", GitHubToken: "gho_x")));

        var calls = api.Calls;
        Assert.Contains(calls, c => c is ("POST", "/api/v1/namespaces/mk/pods"));
        Assert.Contains(calls, c => c is ("POST", "/api/v1/namespaces/mk/services"));
        Assert.Equal(2, calls.Count(c => c is ("POST", "/apis/networking.k8s.io/v1/namespaces/mk/networkpolicies")));

        // the sandbox reaches everything by the broker Service name (its stable in-namespace DNS)
        Assert.Equal("sbx-1-broker", e.ContainerName);
        Assert.Equal("http://sbx-1-broker:3128", e.ProxyUrl);
        Assert.Equal("http://sbx-1-broker:3130", e.ModelUrls!["anthropic"]);
        Assert.Equal("http://sbx-1-broker:3132", e.GitHubApiUrl);
    }

    [Fact]
    public async Task StopAsync_deletes_the_service_pod_and_networkpolicies()
    {
        using var api = new FakeKubeApi();

        await Broker(api).StopAsync(Guid.Empty, new BrokerEndpoint("", "sbx-1-broker", "", ""));

        var calls = api.Calls;
        Assert.Contains(calls, c => c is ("DELETE", "/api/v1/namespaces/mk/services/sbx-1-broker"));
        Assert.Contains(calls, c => c is ("DELETE", "/api/v1/namespaces/mk/pods/sbx-1-broker"));
        Assert.Contains(calls, c => c is ("DELETE", "/apis/networking.k8s.io/v1/namespaces/mk/networkpolicies"));
    }
}
