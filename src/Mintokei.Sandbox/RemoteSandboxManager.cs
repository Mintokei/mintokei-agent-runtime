using Microsoft.Extensions.Logging;
using Mintokei.Sandbox.Docker;

namespace Mintokei.Sandbox;

/// <summary>
/// One-call facade for the remote-worker sandbox path — the analogue of <see cref="SandboxManager"/> for a
/// container dispatched to a CHOSEN worker. <see cref="LaunchAsync"/> bundles the always-the-same mechanical
/// steps (probe Docker → prepare credentials → build the spec → <c>docker run</c> there → wait for the
/// in-container runner to connect back) and hands you a <see cref="RemoteSandboxSession"/> whose disposal
/// recycles everything it created.
///
/// Two credential postures, chosen by the resolved profile's <see cref="SandboxEgress"/>:
/// <list type="bullet">
///   <item><b>Open/Proxy</b> — stage uid-readable credential copies on the worker and mount them (the default).</item>
///   <item><b>Broker</b> — start a per-session broker (<see cref="ISandboxBroker"/>): a deny-by-default
///     <c>--internal</c> network + a broker container that injects short-lived, scoped credentials, so NOTHING
///     is seeded into the box. Requires a registered <see cref="ISandboxBroker"/>; fails closed otherwise.</item>
/// </list>
///
/// The two things it deliberately leaves to the caller carry PRODUCT policy: building the
/// <see cref="SandboxSessionRequest"/> (which worker, creds, repos, backend URLs) and dispatching the actual
/// session (via your control plane, using <see cref="RemoteSandboxSession.MachineId"/>). Presence is checked
/// through the caller-supplied <c>isRunnerConnected</c> delegate, so this stays free of any control-plane dep.
/// </summary>
public sealed class RemoteSandboxManager(
    RemoteDockerSandboxRuntime runtime,
    SandboxCredentialStager stager,
    SandboxSpecFactory specFactory,
    SandboxProfileResolver profiles,
    ILogger<RemoteSandboxManager> logger,
    ISandboxBrokerSecretsProvider secretsProvider,
    ISandboxBroker? broker = null)
{
    /// <summary>
    /// Provision <paramref name="request"/> as a sandbox on <paramref name="workerId"/> and return once its
    /// runner (<paramref name="sandboxMachineId"/>) has connected back. In broker mode the profile's egress
    /// allowlist plus <paramref name="brokerSecrets"/> (git creds / model auth) drive the broker; the secrets
    /// never enter the sandbox. Throws <see cref="SandboxRuntimeException"/> (after cleaning up) if Docker is
    /// missing, provisioning fails, or the container exits / never comes online.
    /// </summary>
    public async Task<RemoteSandboxSession> LaunchAsync(
        Guid workerId,
        Guid sandboxMachineId,
        SandboxSessionRequest request,
        Func<Guid, bool> isRunnerConnected,
        string? profile = null,
        SandboxBrokerSecrets? brokerSecrets = null,
        int onlineTimeoutSeconds = 60,
        bool waitForOnline = true,
        CancellationToken ct = default)
    {
        if (!await runtime.ProbeDockerAsync(workerId, ct))
            throw new SandboxRuntimeException($"worker {workerId} has no working Docker on PATH.");

        var resolved = profiles.Resolve(sessionOverride: profile);
        var brokered = resolved.Egress == SandboxEgress.Broker;
        BrokerEndpoint? endpoint = null;

        if (brokered)
        {
            if (broker is null)
                throw new SandboxRuntimeException(
                    $"profile '{resolved.Name}' requests broker egress but no ISandboxBroker is registered — refusing to launch (fail-closed).");
            // Nothing is staged into the BOX under broker egress — but the broker itself still has to read the
            // credentials from somewhere. Precedence:
            //   1. an explicit brokerSecrets arg (caller knows best),
            //   2. this session's needs → a per-session copy staged on the runner for the BROKER uid, mounted
            //      only into the broker (the token is read there and never crosses the control plane),
            //   3. the registered provider (host-level locations — the seam the pool/K8s path uses).
            SandboxBrokerSecrets? secrets = brokerSecrets;
            if (secrets is null && request.Broker is { } needs)
            {
                var brokerCreds = await stager.StageAsync(workerId, request.Name, new SandboxSeedSources(
                    request.ClaudeConfigHostDir, request.ClaudeConfigJsonHostFile,
                    request.CodexConfigHostDir, request.GitCredentialsHostDir), ct, uid: SandboxImage.BrokerUid);
                secrets = SandboxBrokerSecrets.FromStagedCredentials(needs, brokerCreds);
            }
            secrets ??= await secretsProvider.ResolveAsync(request, resolved, ct);

            // The session's own allowlist wins over the profile's — the same precedence SandboxSpecFactory
            // applies, so the broker ENFORCES exactly what the sandbox was built for. Taking the profile-wide
            // list here would hand a tool a wider egress than its spec asked for.
            var allowlist = request.Broker?.Allowlist is { Count: > 0 } perSession ? perSession : resolved.EgressAllowlist;
            endpoint = await broker.StartAsync(workerId,
                new SandboxBrokerRequest(request.Name, allowlist, secrets), ct);
        }
        else
        {
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
        }

        var spec = specFactory.Build(resolved, request);
        if (brokered)
            spec = SandboxBrokerWiring.Apply(spec, endpoint!);

        // Durable working tree: back /repos with a named volume keyed by PersistentWorkspaceKey, so the whole
        // tree — every repo in the session, plus the agent-CLI transcript the entrypoint symlinks onto it —
        // survives this container being recycled. The volume outlives the container; the embedder's reaper GCs
        // it (ListWorkspaceVolumesAsync / RemoveVolumeAsync) once whatever it is keyed by is finished.
        //
        // The K8s backend honours the same key by mounting a PVC from the spec, which it can do because it
        // creates the PVC itself. Docker volumes have to be created before `docker run`, hence this step —
        // without it the key would be silently ignored on this path and a recycled session would come back
        // with an empty tree and no transcript to --resume from.
        //
        // Only when the session actually has repos: with no working tree there is nothing to keep.
        if (request.PersistentWorkspaceKey is { } workspaceKey && spec.Env.ContainsKey(SandboxSpecFactory.ReposEnvVar))
        {
            var volumeName = RemoteDockerSandboxRuntime.WorkspaceVolumeName(workspaceKey);
            try
            {
                await runtime.EnsureWorkspaceVolumeAsync(workerId, volumeName, workspaceKey, ct);
            }
            catch (SandboxRuntimeException)
            {
                await CleanupSideAsync(workerId, request.Name, endpoint);
                throw;
            }

            spec = spec with { Mounts = [.. spec.Mounts, new SandboxMount(volumeName, SandboxSpecFactory.RepoRoot, ReadOnly: false)] };
            logger.LogInformation("Persisting workspace for key {Key} on volume {Volume} (mounted at {Path})",
                workspaceKey, volumeName, SandboxSpecFactory.RepoRoot);
        }

        SandboxHandle handle;
        try
        {
            handle = await runtime.ProvisionAsync(workerId, spec, ct);
        }
        catch
        {
            await CleanupSideAsync(workerId, request.Name, endpoint); // don't leave the broker / staged creds behind
            throw;
        }

        // The caller can own the wait instead (SandboxProvisioner does: it waits on the control plane's
        // RunnerConnected event and records the pod_ready / runner_enroll split). Hand back the live session
        // and let it decide — it still holds everything needed to recycle on failure.
        if (!waitForOnline)
            return new RemoteSandboxSession(runtime, stager, broker, endpoint, workerId, sandboxMachineId, request.Name, handle);

        // Wait (bounded) for the in-container runner to connect back, bailing early if the container exits first
        // (usually a repo-clone / git-creds error) and surfacing its logs.
        var ticks = Math.Max(1, onlineTimeoutSeconds * 2); // 500 ms per tick
        for (var i = 0; i < ticks; i++)
        {
            if (isRunnerConnected(sandboxMachineId))
            {
                logger.LogInformation("remote sandbox {Name} (machine {MachineId}) online on worker {Worker}{Mode}",
                    request.Name, sandboxMachineId, workerId, brokered ? " (broker egress)" : "");
                return new RemoteSandboxSession(runtime, stager, broker, endpoint, workerId, sandboxMachineId, request.Name, handle);
            }

            var status = await runtime.GetStatusAsync(workerId, handle, ct);
            if (status.State is SandboxState.Exited or SandboxState.NotFound)
            {
                var logs = await runtime.GetLogsAsync(workerId, handle, 40, ct);
                await RecycleAsync(workerId, request.Name, handle, endpoint);
                throw new SandboxRuntimeException($"sandbox '{request.Name}' exited before its runner connected.\n{logs}");
            }

            await Task.Delay(500, ct);
        }

        await RecycleAsync(workerId, request.Name, handle, endpoint);
        throw new SandboxRuntimeException($"sandbox '{request.Name}' did not come online within {onlineTimeoutSeconds}s.");
    }


    private async Task RecycleAsync(Guid workerId, string name, SandboxHandle handle, BrokerEndpoint? endpoint)
    {
        await runtime.StopAsync(workerId, handle);          // best-effort (never throws)
        await CleanupSideAsync(workerId, name, endpoint);
    }

    // Tear down the credential side: the broker (broker mode) or the staged credential copy (open/proxy).
    /// <summary>Tear down everything a launch put on the worker. BOTH the broker and the staged credentials —
    /// a brokered session stages a per-session copy for the broker uid, so treating these as either/or would
    /// leave a copy of the model token on the worker after the session is gone.</summary>
    private async Task CleanupSideAsync(Guid workerId, string name, BrokerEndpoint? endpoint)
    {
        if (endpoint is not null && broker is not null) await broker.StopAsync(workerId, endpoint);
        await stager.RemoveAsync(workerId, name);
    }
}

/// <summary>
/// A live sandbox on a worker. Carries the <see cref="MachineId"/> to dispatch a session to (via your control
/// plane); disposing it one-shot recycles the container plus its credential side — the per-session broker
/// (broker egress) or the staged credential copy (open/proxy) — all on the worker.
/// </summary>
public sealed class RemoteSandboxSession(
    RemoteDockerSandboxRuntime runtime,
    SandboxCredentialStager stager,
    ISandboxBroker? broker,
    BrokerEndpoint? brokerEndpoint,
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

    /// <summary>One-shot recycle: stop the container, then tear down the broker (+ its network) or the staged
    /// credential copy — whichever this session used — all on the worker.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await runtime.StopAsync(workerId, handle);
        // Broker AND staged credentials — not either/or: a brokered session stages a per-session copy for the
        // broker uid, so skipping the stager here would leave the model token on the worker after teardown.
        if (brokerEndpoint is not null && broker is not null) await broker.StopAsync(workerId, brokerEndpoint);
        await stager.RemoveAsync(workerId, name);
    }
}
