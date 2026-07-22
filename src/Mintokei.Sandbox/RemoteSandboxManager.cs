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
            // The broker holds the secrets and provides egress; nothing is staged into the box.
            endpoint = await broker.StartAsync(workerId,
                new SandboxBrokerRequest(request.Name, resolved.EgressAllowlist, brokerSecrets), ct);
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
            spec = WithBrokerWiring(spec, endpoint!);

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

    // Inject the broker's runtime-resolved address into the spec: join its --internal net, route egress through
    // its proxy, and hand the sandbox the git-mint + model base URLs (env the entrypoint / agent CLI read).
    private static SandboxSpec WithBrokerWiring(SandboxSpec spec, BrokerEndpoint e)
    {
        var env = new Dictionary<string, string>(spec.Env) { ["MINTOKEI_BROKER_CRED_URL"] = e.GitMintUrl };
        // The broker itself must NOT be reached through the CONNECT proxy: the git-mint (:3129) and model
        // reverse-proxy (:3130) are PLAINTEXT services on the broker host, but DockerCommand sets HTTP(S)_PROXY to
        // the broker's CONNECT proxy (:3128). A client that honors HTTP_PROXY (e.g. Claude Code / undici) would
        // otherwise forward the plaintext model call THROUGH the CONNECT proxy, which only does CONNECT → 501/hang.
        // Exempt the broker host so those calls go direct; external egress still flows through the proxy+allowlist.
        env["NO_PROXY"] = env["no_proxy"] = e.ContainerName;
        if (e.ModelUrl is not null)
        {
            env["ANTHROPIC_BASE_URL"] = e.ModelUrl;
            env["OPENAI_BASE_URL"] = e.ModelUrl;
        }
        return spec with { NetworkName = e.NetworkName, EgressProxyUrl = e.ProxyUrl, Env = env };
    }

    private async Task RecycleAsync(Guid workerId, string name, SandboxHandle handle, BrokerEndpoint? endpoint)
    {
        await runtime.StopAsync(workerId, handle);          // best-effort (never throws)
        await CleanupSideAsync(workerId, name, endpoint);
    }

    // Tear down the credential side: the broker (broker mode) or the staged credential copy (open/proxy).
    private async Task CleanupSideAsync(Guid workerId, string name, BrokerEndpoint? endpoint)
    {
        if (endpoint is not null && broker is not null) await broker.StopAsync(workerId, endpoint);
        else await stager.RemoveAsync(workerId, name);
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
        if (brokerEndpoint is not null && broker is not null) await broker.StopAsync(workerId, brokerEndpoint);
        else await stager.RemoveAsync(workerId, name);
    }
}
