using Microsoft.Extensions.Logging;
using Mintokei.Sandbox.Docker;

namespace Mintokei.Sandbox;

/// <summary>
/// One-call facade for the remote-worker sandbox path — the analogue of <see cref="SandboxManager"/> for a
/// container dispatched to a CHOSEN worker. <see cref="LaunchAsync"/> bundles the always-the-same mechanical
/// steps (probe Docker → stage credentials uid-readable on the worker → build the spec → <c>docker run</c>
/// there → wait for the in-container runner to connect back) and hands you a <see cref="RemoteSandboxSession"/>
/// whose disposal recycles (stop container + remove the staged creds).
///
/// The two things it deliberately leaves to the caller are the ones that carry PRODUCT policy: building the
/// <see cref="SandboxSessionRequest"/> (which worker, creds, repos, backend URLs — minted from your enrollment)
/// and dispatching the actual session (via your control plane, using <see cref="RemoteSandboxSession.MachineId"/>).
/// Presence is checked through the caller-supplied <c>isRunnerConnected</c> delegate, so this stays free of any
/// control-plane dependency.
/// </summary>
public sealed class RemoteSandboxManager(
    RemoteDockerSandboxRuntime runtime,
    SandboxCredentialStager stager,
    SandboxSpecFactory specFactory,
    SandboxProfileResolver profiles,
    ILogger<RemoteSandboxManager> logger)
{
    /// <summary>
    /// Provision <paramref name="request"/> as a sandbox on <paramref name="workerId"/> and return once its
    /// runner (<paramref name="sandboxMachineId"/>) has connected back. Throws <see cref="SandboxRuntimeException"/>
    /// (after cleaning up) if Docker is missing, provisioning fails, or the container exits / never comes online.
    /// </summary>
    public async Task<RemoteSandboxSession> LaunchAsync(
        Guid workerId,
        Guid sandboxMachineId,
        SandboxSessionRequest request,
        Func<Guid, bool> isRunnerConnected,
        string? profile = null,
        int onlineTimeoutSeconds = 60,
        CancellationToken ct = default)
    {
        if (!await runtime.ProbeDockerAsync(workerId, ct))
            throw new SandboxRuntimeException($"worker {workerId} has no working Docker on PATH.");

        // Stage the creds into a uid-readable per-session copy on the worker and mount THAT — the non-root
        // container can't read the worker's own root-owned creds directly.
        var staged = await stager.StageAsync(workerId, request.Name, new SandboxSeedSources(
            request.ClaudeConfigHostDir, request.ClaudeConfigJsonHostFile,
            request.CodexConfigHostDir, request.GitCredentialsHostDir), ct);
        request = request with
        {
            ClaudeConfigHostDir = staged.ClaudeConfigDir,
            ClaudeConfigJsonHostFile = staged.ClaudeConfigJsonFile,
            CodexConfigHostDir = staged.CodexConfigDir,
            GitCredentialsHostDir = staged.GitCredentialsDir,
        };

        var spec = specFactory.Build(profiles.Resolve(sessionOverride: profile), request);

        SandboxHandle handle;
        try
        {
            handle = await runtime.ProvisionAsync(workerId, spec, ct);
        }
        catch
        {
            await stager.RemoveAsync(workerId, request.Name); // don't leave the staged creds behind
            throw;
        }

        // Wait (bounded) for the in-container runner to connect back, bailing early if the container exits first
        // (usually a repo-clone / git-creds error) and surfacing its logs.
        var ticks = Math.Max(1, onlineTimeoutSeconds * 2); // 500 ms per tick
        for (var i = 0; i < ticks; i++)
        {
            if (isRunnerConnected(sandboxMachineId))
            {
                logger.LogInformation("remote sandbox {Name} (machine {MachineId}) online on worker {Worker}",
                    request.Name, sandboxMachineId, workerId);
                return new RemoteSandboxSession(runtime, stager, workerId, sandboxMachineId, request.Name, handle);
            }

            var status = await runtime.GetStatusAsync(workerId, handle, ct);
            if (status.State is SandboxState.Exited or SandboxState.NotFound)
            {
                var logs = await runtime.GetLogsAsync(workerId, handle, 40, ct);
                await RecycleAsync(workerId, request.Name, handle);
                throw new SandboxRuntimeException($"sandbox '{request.Name}' exited before its runner connected.\n{logs}");
            }

            await Task.Delay(500, ct);
        }

        await RecycleAsync(workerId, request.Name, handle);
        throw new SandboxRuntimeException($"sandbox '{request.Name}' did not come online within {onlineTimeoutSeconds}s.");
    }

    private async Task RecycleAsync(Guid workerId, string name, SandboxHandle handle)
    {
        await runtime.StopAsync(workerId, handle);   // best-effort (never throws)
        await stager.RemoveAsync(workerId, name);    // best-effort (never throws)
    }
}

/// <summary>
/// A live sandbox on a worker. Carries the <see cref="MachineId"/> to dispatch a session to (via your control
/// plane); disposing it one-shot recycles the container + staged credentials on the worker.
/// </summary>
public sealed class RemoteSandboxSession(
    RemoteDockerSandboxRuntime runtime,
    SandboxCredentialStager stager,
    Guid workerId,
    Guid machineId,
    string name,
    SandboxHandle handle) : IAsyncDisposable
{
    private int _disposed;

    /// <summary>The sandbox runner's machine id — dispatch the session to it through your control plane.</summary>
    public Guid MachineId => machineId;

    /// <summary>The provisioned container handle on the worker.</summary>
    public SandboxHandle Handle => handle;

    /// <summary>One-shot recycle: stop the container + remove the staged credential copy, both on the worker.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await runtime.StopAsync(workerId, handle);
        await stager.RemoveAsync(workerId, name);
    }
}
