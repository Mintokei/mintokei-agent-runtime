using System.Net;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mintokei.Sandbox.Kubernetes;

/// <summary>
/// <see cref="ISandboxBroker"/> over the Kubernetes API — the K8s analogue of
/// <see cref="Docker.RemoteSandboxBroker"/>. <see cref="StartAsync"/> creates the per-session broker Pod + a
/// ClusterIP Service (the sandbox's stable DNS handle) + a deny-by-default egress NetworkPolicy on the sandbox
/// and a companion policy on the broker; <see cref="StopAsync"/> removes them. The sandbox reaches the broker by
/// its Service name; the NetworkPolicy makes the broker its ONLY route out. Requires a NetworkPolicy-enforcing
/// CNI (calico / cilium / k3s kube-router) and RBAC to create pods/services/networkpolicies (see
/// <c>k8s/sandbox-broker-rbac.yaml</c>).
/// </summary>
public sealed class KubernetesSandboxBroker(
    IKubernetes client,
    IOptions<SandboxOptions> options,
    ILogger<KubernetesSandboxBroker> logger) : ISandboxBroker
{
    private readonly SandboxOptions _options = options.Value;
    private readonly string _namespace = string.IsNullOrWhiteSpace(options.Value.KubernetesNamespace)
        ? "default"
        : options.Value.KubernetesNamespace;

    public async Task<BrokerEndpoint> StartAsync(Guid workerId, SandboxBrokerRequest request, CancellationToken ct = default)
    {
        var brokerName = KubernetesBrokerSpec.BrokerName(request.SessionName);
        var brokerEnv = BrokerEnvironment.Build(request);

        // The sandbox reaches the broker by its Service name (short name resolves in-namespace via kube-dns).
        var modelUrls = brokerEnv.ModelPorts.ToDictionary(kv => kv.Key, kv => $"http://{brokerName}:{kv.Value}");
        var githubApiUrl = brokerEnv.HasGitHub ? $"http://{brokerName}:{BrokerEnvironment.GitHubPort}" : null;
        var endpoint = new BrokerEndpoint(
            NetworkName: "", // no Docker network in K8s; containment is the NetworkPolicy
            ContainerName: brokerName,
            ProxyUrl: $"http://{brokerName}:3128",
            GitMintUrl: $"http://{brokerName}:3129/git-credential",
            ModelUrls: modelUrls.Count > 0 ? modelUrls : null,
            GitHubApiUrl: githubApiUrl);

        try
        {
            await client.CoreV1.CreateNamespacedPodAsync(
                KubernetesBrokerSpec.BuildBrokerPod(request.SessionName, _options.BrokerImage, brokerEnv.Env), _namespace, cancellationToken: ct);
            await client.CoreV1.CreateNamespacedServiceAsync(
                KubernetesBrokerSpec.BuildBrokerService(request.SessionName), _namespace, cancellationToken: ct);
            // The containment: deny-by-default egress on the sandbox (broker + DNS only) + scope the broker.
            await client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                KubernetesBrokerSpec.BuildSandboxEgressPolicy(request.SessionName), _namespace, cancellationToken: ct);
            await client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                KubernetesBrokerSpec.BuildBrokerPolicy(request.SessionName), _namespace, cancellationToken: ct);
        }
        catch (HttpOperationException ex)
        {
            await StopAsync(workerId, endpoint, ct); // don't leave half a broker (or an unenforced policy) behind
            throw new SandboxRuntimeException(
                $"creating broker '{brokerName}' in namespace '{_namespace}' failed " +
                $"({(int?)ex.Response?.StatusCode}): {ex.Response?.Content?.Trim()}", ex);
        }

        var extras = new List<string>();
        if (endpoint.ModelUrls is not null) extras.Add($"model-inject({string.Join(",", endpoint.ModelUrls.Keys)})");
        if (endpoint.GitHubApiUrl is not null) extras.Add("github-token");
        logger.LogInformation("broker {Broker} up in ns {Namespace} ({N} allow-rules{Extra})",
            brokerName, _namespace, request.EgressAllowlist.Count, extras.Count == 0 ? "" : ", " + string.Join(", ", extras));
        return endpoint;
    }

    public async Task StopAsync(Guid workerId, BrokerEndpoint endpoint, CancellationToken ct = default)
    {
        var brokerName = endpoint.ContainerName;
        var session = brokerName.EndsWith("-broker", StringComparison.Ordinal) ? brokerName[..^"-broker".Length] : brokerName;

        // Service has no deletecollection verb → delete by name; Pod likewise; the two NetworkPolicies by label.
        await Try(() => client.CoreV1.DeleteNamespacedServiceAsync(brokerName, _namespace, cancellationToken: ct), "service");
        await Try(() => client.CoreV1.DeleteNamespacedPodAsync(brokerName, _namespace, gracePeriodSeconds: 0, cancellationToken: ct), "pod");
        await Try(() => client.NetworkingV1.DeleteCollectionNamespacedNetworkPolicyAsync(
            _namespace, labelSelector: $"{KubernetesBrokerSpec.SessionLabel}={session}", cancellationToken: ct), "networkpolicies");
    }

    private async Task Try(Func<Task> op, string what)
    {
        try { await op(); }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound) { /* already gone */ }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "broker {What} delete failed", what); }
    }
}
