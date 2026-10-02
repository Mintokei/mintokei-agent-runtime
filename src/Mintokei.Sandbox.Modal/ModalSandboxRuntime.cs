using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mintokei.Sandbox.Modal;

/// <summary>Modal's domain allowlist permits TLS on port 443. Null leaves default outbound access.
/// This is distinct from the broker/CONNECT proxy modes of Docker and Kubernetes.</summary>
public sealed record ModalSandboxPolicy(IReadOnlyList<string>? NetworkDomains = null,
    ModalNativeAccount? NativeAccount = null, bool IsolateWorkspace = false);

public sealed record ModalLaunchResult(SandboxHandle Handle, bool NetworkRestricted, bool WorkspaceIsolated);

/// <summary>Optional durable host seam. Persist launch intent before the cloud call, then the returned ID
/// before interpreting policy confirmations. Throwing aborts admission; it does not assert cleanup.</summary>
public interface IModalSandboxLifecycle
{
    Task BeforeLaunchAsync(SandboxSpec spec, CancellationToken ct);
    Task LaunchedAsync(ModalLaunchResult result, CancellationToken ct);
}

/// <summary>Product-free Modal backend. The caller owns durable leases, retries and reconciliation.
/// A name-only handle represents an uncertain launch; absence by name cannot prove it never launched.</summary>
public sealed class ModalSandboxRuntime(IModalClient client, ModalSandboxPolicy? policy = null,
    IModalSandboxLifecycle? lifecycle = null) : ISandboxRuntime
{
    public string Backend => "modal";
    public async Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        Validate(spec, policy);
        if (lifecycle != null) await lifecycle.BeforeLaunchAsync(spec, ct);
        var result = await client.CallAsync("launch", new
        {
            spec = new { spec.Name, spec.Args, spec.Env, spec.Limits }, networkDomains = policy?.NetworkDomains,
            nativeAccount = policy?.NativeAccount, isolateWorkspace = policy?.IsolateWorkspace ?? false
        }, ct);
        var id = result.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(id)) throw new SandboxRuntimeException("Modal returned no sandbox identity.");
        var launched = new ModalLaunchResult(new(id, spec.Name, Backend), Flag(result, "networkRestricted"), Flag(result, "workspaceIsolated"));
        if (lifecycle != null) await lifecycle.LaunchedAsync(launched, ct);
        if (policy?.NetworkDomains != null && !launched.NetworkRestricted)
            throw new SandboxRuntimeException("The sandbox did not confirm the requested network policy.");
        if (policy?.IsolateWorkspace == true && !launched.WorkspaceIsolated)
            throw new SandboxRuntimeException("The sandbox did not confirm workspace isolation.");
        return launched.Handle;
    }
    public async Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct = default)
    {
        var result = await client.CallAsync("status", Identity(handle), ct);
        return new(State(result), result.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var n) ? n : null);
    }
    public async Task StopAsync(SandboxHandle handle, CancellationToken ct = default)
    {
        var result = await client.CallAsync("stop", Identity(handle), ct);
        var state = State(result);
        if (state is not (SandboxState.Exited or SandboxState.NotFound) || state == SandboxState.NotFound && handle.Id == handle.Name)
            throw new SandboxRuntimeException("Sandbox termination is unconfirmed; reconcile the launch before releasing its lease.");
    }
    public async Task<IReadOnlyList<SandboxHandle>> ListManagedAsync(CancellationToken ct = default)
    {
        var result = await client.CallAsync("list", new { }, ct);
        return result.GetProperty("sandboxes").EnumerateArray()
            .Select(s => new SandboxHandle(s.GetProperty("id").GetString()!, s.GetProperty("name").GetString()!, Backend)).ToArray();
    }
    private static object Identity(SandboxHandle handle) => new { id = handle.Id == handle.Name ? null : handle.Id, name = handle.Name };
    private static bool Flag(JsonElement result, string name) => result.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
    private static SandboxState State(JsonElement result) => result.GetProperty("state").GetString() switch
    {
        "running" => SandboxState.Running, "exited" => SandboxState.Exited,
        "not_found" => SandboxState.NotFound, _ => SandboxState.Unknown
    };
    public static void Validate(SandboxSpec spec, ModalSandboxPolicy? policy = null)
    {
        if (policy?.IsolateWorkspace == true && (policy.NativeAccount == null || policy.NetworkDomains == null) ||
            policy?.NativeAccount != null && policy.NetworkDomains != null && !policy.IsolateWorkspace)
            throw new SandboxRuntimeException("Restricted native authentication requires isolated workspace execution.");
        if (spec.Mounts.Count != 0 || spec.PersistentWorkspaceKey != null || spec.Egress != SandboxEgress.Open ||
            spec.ReadOnlyRootfs || spec.AddHostGateway || spec.RuntimeClass != "runsc" ||
            spec.BrokerSecrets != null || spec.NetworkName != null || spec.EgressProxyUrl != null ||
            spec.EgressAllowlist.Count != 0 || spec.AdmittedTools.Count != 0 || !spec.Tmpfs.SequenceEqual(new[] { "/data" }) ||
            spec.Env.ContainsKey(SandboxSpecFactory.ReposEnvVar) || spec.Env.ContainsKey("SANDBOX_REPO_URL"))
            throw new SandboxRuntimeException("This Modal backend supports a managed gVisor image, ephemeral workspace and optional domain restrictions; the requested capability is unsupported.");
        // Modal CPU is physical cores (two vCPU each); SandboxResources uses vCPU as Docker/Kubernetes do.
        if (!double.IsFinite(spec.Limits.CpuLimit) || spec.Limits.CpuLimit <= 0 || spec.Limits.MemoryLimitBytes <= 0 ||
            spec.Limits.CpuReserve is { } reserve && (!double.IsFinite(reserve) || reserve <= 0 || reserve > spec.Limits.CpuLimit) ||
            spec.Limits.MemoryReserveBytes is { } memory && (memory <= 0 || memory > spec.Limits.MemoryLimitBytes))
            throw new SandboxRuntimeException("Invalid Modal resource limits.");
        if (policy?.NetworkDomains is { } domains && (domains.Count is < 1 or > 36 || domains.Any(d =>
            d.Length > 253 || !Regex.IsMatch(d, @"\A(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}\z"))))
            throw new SandboxRuntimeException("Invalid Modal network domains.");
    }
}
