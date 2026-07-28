namespace Mintokei.Sandbox.Docker;

/// <summary>
/// One worker's view of <see cref="RemoteDockerSandboxRuntime"/>, behind the same interfaces every other
/// backend implements.
///
/// The nested path is machine-targeted — every method takes the worker's machine id — which is why it is
/// deliberately NOT an <see cref="ISandboxRuntime"/>: that seam is host-agnostic, and at the point where a
/// host resolves its single backend there is no such thing as "the worker". That decision stands.
///
/// Its cost is what this type pays off. Nothing relates
/// <c>RemoteDockerSandboxRuntime.GetAdmittedToolsAsync(machineId, handle, ct)</c> to
/// <c>DockerSandboxRuntime.GetAdmittedToolsAsync(handle, ct)</c> — same operation, different shape — so a
/// capability can land on one backend and go missing on another with nothing to notice. Three gaps were found
/// exactly that way (see <c>docs/sandbox-backend-capabilities.md</c>), each surfacing far from its cause.
///
/// Binding the machine id restores the relation without weakening the seam: a caller that HAS a worker gets
/// the ordinary interfaces, and the capability-parity test can hold all three backends to one list.
/// </summary>
public sealed class WorkerBoundSandboxRuntime(RemoteDockerSandboxRuntime inner, Guid hostMachineId)
    : ISandboxRuntime, ISandboxLogSource, ISandboxAdmissionSource, ISandboxWorkspaceStore
{
    /// <summary>The worker this view is bound to.</summary>
    public Guid HostMachineId => hostMachineId;

    public string Backend => RemoteDockerSandboxRuntime.Backend;

    public Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default)
        => inner.ProvisionAsync(hostMachineId, spec, ct);

    public Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct = default)
        => inner.GetStatusAsync(hostMachineId, handle, ct);

    public Task StopAsync(SandboxHandle handle, CancellationToken ct = default)
        => inner.StopAsync(hostMachineId, handle, ct);

    public async Task<IReadOnlyList<SandboxHandle>> ListManagedAsync(CancellationToken ct = default)
    {
        // The remote call reports container NAMES (and, separately, networks — which are not sandboxes and so
        // are dropped here). Docker looks a sandbox up by name, so name doubles as id; same convention as
        // ProvisionedSandbox.Handle.
        var managed = await inner.ListManagedAsync(hostMachineId, ct);
        return [.. managed.Containers.Select(name => new SandboxHandle(name, name, Backend))];
    }

    public Task<string> GetLogsAsync(SandboxHandle handle, int tailLines = 40, CancellationToken ct = default)
        => inner.GetLogsAsync(hostMachineId, handle, tailLines, ct);

    public Task<IReadOnlyList<string>> GetAdmittedToolsAsync(SandboxHandle handle, CancellationToken ct = default)
        => inner.GetAdmittedToolsAsync(hostMachineId, handle, ct);

    public async Task<IReadOnlyList<Guid>> ListPersistentWorkspaceKeysAsync(CancellationToken ct = default)
    {
        // The remote call returns every managed volume name; only the ones that parse as a workspace key are
        // workspace stores, so a volume created for anything else is not reported as one.
        var keys = new List<Guid>();
        foreach (var name in await inner.ListWorkspaceVolumesAsync(hostMachineId, ct))
            if (RemoteDockerSandboxRuntime.TryParseWorkspaceKey(name, out var key))
                keys.Add(key);
        return keys;
    }

    public Task<bool> RemovePersistentWorkspaceAsync(Guid key, CancellationToken ct = default)
        => inner.RemoveVolumeAsync(hostMachineId, RemoteDockerSandboxRuntime.WorkspaceVolumeName(key), ct);
}
