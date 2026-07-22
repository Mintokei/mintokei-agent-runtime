using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mintokei.Sandbox.Kubernetes;

/// <summary>
/// <see cref="ISandboxRuntime"/> over the Kubernetes API — one Pod per session, the containerd/k3s
/// backend the seam was designed for ("docker" now, "k8s" later). Talks to the API server directly with
/// the typed client (in-cluster ServiceAccount auth), so there is no docker socket and no CLI in the
/// image. Selected by <c>Sandbox:Backend=kubernetes</c>; the pool/lifecycle/reaper above the seam are
/// unchanged. Pods land in <see cref="SandboxOptions.KubernetesNamespace"/>.
/// </summary>
public sealed class KubernetesSandboxRuntime(
    IKubernetes client,
    IOptions<SandboxOptions> options,
    ILogger<KubernetesSandboxRuntime> logger,
    ISandboxBroker? broker = null) : ISandboxRuntime, ISandboxLogSource
{
    private readonly string _namespace = string.IsNullOrWhiteSpace(options.Value.KubernetesNamespace)
        ? "default"
        : options.Value.KubernetesNamespace;
    private readonly string? _imagePullPolicy = options.Value.KubernetesImagePullPolicy;

    public string Backend => "kubernetes";

    public async Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        // Broker egress: start the per-session broker (Pod + Service + deny-by-default NetworkPolicy) FIRST, then
        // wire the sandbox Pod to reach only it. Fail closed if no broker is registered. If the sandbox Pod fails
        // to create, tear the broker back down so nothing is orphaned.
        if (spec.Egress == SandboxEgress.Broker)
        {
            if (broker is null)
                throw new SandboxRuntimeException(
                    "broker egress requested but no ISandboxBroker is registered — refusing to launch (fail-closed).");

            var endpoint = await broker.StartAsync(
                Guid.Empty, new SandboxBrokerRequest(spec.Name, spec.EgressAllowlist, spec.BrokerSecrets), ct);
            try
            {
                return await CreatePodAsync(SandboxBrokerWiring.Apply(spec, endpoint), ct);
            }
            catch
            {
                await broker.StopAsync(Guid.Empty, endpoint, ct);
                throw;
            }
        }

        return await CreatePodAsync(spec, ct);
    }

    private async Task<SandboxHandle> CreatePodAsync(SandboxSpec spec, CancellationToken ct)
    {
        var pod = KubernetesPodSpec.Build(spec, _imagePullPolicy);

        V1Pod created;
        try
        {
            created = await client.CoreV1.CreateNamespacedPodAsync(pod, _namespace, cancellationToken: ct);
        }
        catch (HttpOperationException ex)
        {
            throw new SandboxRuntimeException(
                $"creating pod '{spec.Name}' in namespace '{_namespace}' failed " +
                $"({(int?)ex.Response?.StatusCode}): {ex.Response?.Content?.Trim()}", ex);
        }

        // The Pod name (== spec.Name) is the stable handle used to inspect/delete; carry the uid as the id.
        var id = created.Metadata?.Uid ?? spec.Name;
        logger.LogInformation("Provisioned sandbox {Name} (pod {Id}) runtimeClass={Runtime} ns={Namespace}{Broker}",
            spec.Name, Short(id), created.Spec?.RuntimeClassName ?? "(node default)", _namespace,
            spec.Egress == SandboxEgress.Broker ? " (broker egress)" : "");
        return new SandboxHandle(id, spec.Name, Backend);
    }

    public async Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct = default)
    {
        V1Pod pod;
        try
        {
            pod = await client.CoreV1.ReadNamespacedPodAsync(handle.Name, _namespace, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return new SandboxStatus(SandboxState.NotFound);
        }
        catch (HttpOperationException ex)
        {
            return new SandboxStatus(SandboxState.Unknown, Detail: ex.Response?.ReasonPhrase);
        }

        var state = MapPhase(pod.Status?.Phase);

        // Surface the runner container's terminated exit code when the Pod has finished, mirroring Docker's
        // State.ExitCode. (First terminated container status; there is only the one sandbox container.)
        var exitCode = pod.Status?.ContainerStatuses?
            .Select(c => c.State?.Terminated?.ExitCode)
            .FirstOrDefault(code => code is not null);

        return new SandboxStatus(state, exitCode);
    }

    public async Task StopAsync(SandboxHandle handle, CancellationToken ct = default)
    {
        try
        {
            // gracePeriodSeconds 0 = delete now (ephemeral single-shot session; nothing to drain).
            await client.CoreV1.DeleteNamespacedPodAsync(
                handle.Name, _namespace, gracePeriodSeconds: 0, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone — mirror DockerSandboxRuntime tolerating "No such object".
        }
        catch (HttpOperationException ex)
        {
            throw new SandboxRuntimeException(
                $"deleting pod '{handle.Name}' in namespace '{_namespace}' failed " +
                $"({(int?)ex.Response?.StatusCode})", ex);
        }

        // Broker mode also created a per-session broker (Pod + Service + NetworkPolicies) — tear it down too.
        // Best-effort and keyed off the pod name, so it's a no-op (404-tolerant) for non-broker sandboxes and
        // also reaps a broker orphaned by a crash between the two creates.
        if (broker is not null)
            await broker.StopAsync(Guid.Empty, new BrokerEndpoint("", KubernetesBrokerSpec.BrokerName(handle.Name), "", ""), ct);

        logger.LogInformation("Stopped sandbox {Name} ({Id}) ns={Namespace}", handle.Name, Short(handle.Id), _namespace);
    }

    public async Task<string> GetLogsAsync(SandboxHandle handle, int tailLines = 40, CancellationToken ct = default)
    {
        try
        {
            using var stream = await client.CoreV1.ReadNamespacedPodLogAsync(
                handle.Name, _namespace, tailLines: tailLines, cancellationToken: ct);
            using var reader = new System.IO.StreamReader(stream);
            return (await reader.ReadToEndAsync(ct)).Trim();
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return string.Empty; // pod already reaped
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "reading pod logs failed for {Name} in {Namespace}", handle.Name, _namespace);
            return string.Empty;
        }
    }

    public async Task<IReadOnlyList<SandboxHandle>> ListManagedAsync(CancellationToken ct = default)
    {
        // Managed sandboxes we launched, EXCLUDING per-session broker Pods (which also carry the managed label,
        // but are a broker's Pod, not a sandbox — the reaper must not treat them as reclaimable sessions).
        var list = await client.CoreV1.ListNamespacedPodAsync(
            _namespace,
            labelSelector: $"{KubernetesPodSpec.ManagedLabel},{KubernetesBrokerSpec.RoleLabel}!={KubernetesBrokerSpec.BrokerRole}",
            cancellationToken: ct);

        return list.Items
            .Where(p => !string.IsNullOrEmpty(p.Metadata?.Name))
            .Select(p => new SandboxHandle(p.Metadata.Uid ?? p.Metadata.Name, p.Metadata.Name, Backend))
            .ToList();
    }

    private static SandboxState MapPhase(string? phase) => phase switch
    {
        "Pending" => SandboxState.Pending,
        "Running" => SandboxState.Running,
        "Succeeded" or "Failed" => SandboxState.Exited,
        _ => SandboxState.Unknown,
    };

    private static string Short(string id) => id[..Math.Min(12, id.Length)];
}
