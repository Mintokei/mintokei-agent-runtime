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
    ISandboxBroker? broker = null) : ISandboxRuntime, ISandboxLogSource, ISandboxWorkspaceStore
{
    private readonly string _namespace = string.IsNullOrWhiteSpace(options.Value.KubernetesNamespace)
        ? "default"
        : options.Value.KubernetesNamespace;
    private readonly string? _imagePullPolicy = options.Value.KubernetesImagePullPolicy;
    // Persistent /repos PVC (resume-after-reap). Storage class null → the cluster default (k3s: local-path, which
    // mkdir's the volume 0777 so the non-root agent can write without an fsGroup/chown dance). Size is generous
    // for a repo + transcripts; local-path is thin (a node dir), so the request is a floor, not a reservation.
    private readonly string? _workspaceStorageClass = options.Value.KubernetesWorkspaceStorageClass;
    private readonly string _workspaceSize = string.IsNullOrWhiteSpace(options.Value.KubernetesWorkspaceStorageSize)
        ? "2Gi" : options.Value.KubernetesWorkspaceStorageSize!;

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
        // Persistent workspace: ensure the per-task PVC exists BEFORE the Pod references it (a re-provision of the
        // same task rebinds the existing one — that is how the working tree + transcript survive the recycle).
        if (spec.PersistentWorkspaceKey is { } wsTask)
            await EnsurePvcAsync(wsTask, ct);

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

    // Create the per-task workspace PVC if it doesn't already exist. Tolerates 409 (already exists) so a
    // re-provision of the same task rebinds the existing claim rather than failing.
    private async Task EnsurePvcAsync(Guid taskId, CancellationToken ct)
    {
        var name = SandboxWorkspaceStore.Name(taskId);
        var pvc = new V1PersistentVolumeClaim
        {
            ApiVersion = "v1",
            Kind = "PersistentVolumeClaim",
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = _namespace,
                Labels = new Dictionary<string, string>
                {
                    [KubernetesPodSpec.ManagedLabel] = "1",
                    [SandboxWorkspaceStore.LabelKey] = taskId.ToString("N"),
                },
            },
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = ["ReadWriteOnce"],
                StorageClassName = _workspaceStorageClass, // null → cluster default StorageClass
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new ResourceQuantity(_workspaceSize) },
                },
            },
        };
        try
        {
            await client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(pvc, _namespace, cancellationToken: ct);
            logger.LogInformation("Created persistent workspace PVC {Name} for task {TaskId}", name, taskId);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            // Already exists — a resumed session rebinds the existing PVC (this is what preserves the transcript).
        }
        catch (HttpOperationException ex)
        {
            throw new SandboxRuntimeException(
                $"creating workspace PVC '{name}' in namespace '{_namespace}' failed " +
                $"({(int?)ex.Response?.StatusCode}): {ex.Response?.Content?.Trim()}", ex);
        }
    }

    // --- ISandboxWorkspaceStore: reaper-driven GC of per-task PVCs ---

    public async Task<IReadOnlyList<Guid>> ListPersistentWorkspaceKeysAsync(CancellationToken ct = default)
    {
        var list = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(
            _namespace, labelSelector: KubernetesPodSpec.ManagedLabel, cancellationToken: ct);
        var ids = new List<Guid>();
        foreach (var name in list.Items.Select(p => p.Metadata?.Name))
            if (name is not null && SandboxWorkspaceStore.TryParseKey(name, out var id))
                ids.Add(id);
        return ids;
    }

    public async Task<bool> RemovePersistentWorkspaceAsync(Guid taskId, CancellationToken ct = default)
    {
        try
        {
            await client.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(
                SandboxWorkspaceStore.Name(taskId), _namespace, cancellationToken: ct);
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return true; // already gone — the caller's post-condition ("not there") holds
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else (RBAC, conflict, API down) left the PVC in place: report failure so the
            // caller doesn't mirror a deletion that didn't happen. Retried on the next sweep.
            logger.LogDebug(ex, "could not delete workspace PVC for {Key}", taskId);
            return false;
        }
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
