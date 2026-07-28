using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mintokei.AgentControlPlane;
using Mintokei.Runner.Host.Server;
using Mintokei.Sandbox.Docker;

namespace Mintokei.Sandbox.Hosting;

/// <summary>
/// Gets you an <b>online sandbox</b> — and nothing more. Mints a one-time enrollment token that pre-creates
/// the ephemeral machine identity, launches the container (locally, in Kubernetes, or on a connected worker),
/// waits for the runner inside it to enroll and connect, and hands back the machine id to dispatch sessions to.
///
/// <para>The caller owns the sandbox's lifetime: <see cref="ProvisionedSandbox.RecycleAsync"/> is explicit,
/// never automatic. That's what lets a one-shot run recycle at the end of a turn while a long-lived product
/// keeps the same sandbox pinned across many turns and reaps it on its own schedule.</para>
///
/// <para>This is the piece worth sharing: binding by pre-created id instead of racing on a name, an
/// event-driven online-wait that bails the moment the container exits during startup (surfacing its logs),
/// per-phase telemetry, and recycling whatever was launched when the wait fails.</para>
/// </summary>
public sealed class SandboxProvisioner(
    IServiceScopeFactory scopeFactory,
    SandboxManager manager,
    ISandboxRuntime runtime,
    IRunnerRegistry runners,
    IOptions<SandboxAgentHostOptions> options,
    ILogger<SandboxProvisioner> logger,
    IServiceProvider services)
{
    /// <summary>
    /// Gate a session joining an EXISTING sandbox. Reads the declaration off the sandbox itself and refuses
    /// unless it admits <paramref name="tool"/>.
    ///
    /// The read is the point: a caller's own record of what a sandbox serves can be stale (API restart, DB
    /// rollback, a second embedder), and the only authority on what the running broker will actually serve is
    /// the sandbox. Provisioning a NEW sandbox never comes through here — there is nobody to protect yet.
    ///
    /// Fails closed when the backend cannot report a declaration at all: without one there is nothing to check
    /// a joining session against, so sharing is unavailable there rather than assumed safe.
    /// </summary>
    public async Task EnsureCanAttachAsync(SandboxHandle handle, string tool, CancellationToken ct = default)
    {
        if (runtime is not ISandboxAdmissionSource source)
            throw new SandboxAdmissionException(
                $"backend '{handle.Backend}' cannot report what sandbox '{handle.Name}' was provisioned to serve, "
                + "so a second session cannot be admitted to it — provision a separate sandbox instead.");

        var admitted = await source.GetAdmittedToolsAsync(handle, ct);

        // An existing sandbox with NO declaration is a single-session sandbox: provisioned before sharing, or by
        // a caller that never opted in. Joining it would put a second session beside one that never agreed to
        // share its broker, so this is the one place an empty declaration must NOT read as "unconstrained".
        if (admitted.Count == 0)
            throw new SandboxAdmissionException(
                $"sandbox '{handle.Name}' carries no tool declaration, so it is single-session — a second "
                + "session cannot join it. Provision a separate sandbox.");

        SandboxAdmission.EnsureAdmits(handle.Name, admitted, tool);
    }
    /// <summary>
    /// Provision a sandbox and wait for it to come online. Everything host-wide comes from the
    /// <c>Sandbox</c> config section; <paramref name="request"/> carries the per-session inputs.
    /// </summary>
    /// <exception cref="SandboxAgentException">Could not be launched, or never came online (carrying the
    /// container's tail logs). Anything that was launched is recycled before this throws.</exception>
    public Task<ProvisionedSandbox> ProvisionAsync(
        SandboxProvisionRequest request, CancellationToken ct = default) =>
        ProvisionAsync(request, configure: null, ct);

    /// <summary>
    /// Provision a sandbox and wait for it to come online, shaping the composed spec via
    /// <paramref name="configure"/>.
    /// </summary>
    /// <param name="request">Profile, repos, broker needs, target host, timeout.</param>
    /// <param name="configure">
    /// Last-word hook over the composed <see cref="SandboxSessionRequest"/>, for what config can't express —
    /// e.g. per-tenant credential paths. Host-wide options are applied first, so a hook only overrides what it
    /// names. <c>Name</c> and <c>EnrollmentToken</c> are re-pinned afterwards: they are the identity the
    /// sandbox is bound to, not policy.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="SandboxAgentException">Could not be launched, or never came online (carrying the
    /// container's tail logs). Anything that was launched is recycled before this throws.</exception>
    public async Task<ProvisionedSandbox> ProvisionAsync(
        SandboxProvisionRequest request,
        Func<SandboxSessionRequest, SandboxSessionRequest>? configure,
        CancellationToken ct = default)
    {
        var o = options.Value;
        var profile = request.Profile ?? o.Profile;
        var name = $"sandbox-{profile}-{Guid.NewGuid().ToString("N")[..12]}";
        var backend = request.HostMachineId is null ? runtime.Backend : "docker-nested";

        using var totalPhase = SandboxTelemetry.Phase("provision.total", backend);

        // Mint the identity FIRST: pre-creating the machine id means the session binds to a known id rather
        // than discovering the runner by name after it enrolls — no races when several sandboxes start at once.
        // IRunnerEnrollment is scoped (it writes the machine row), so take a short-lived scope per call rather
        // than capturing it in this singleton.
        CreateEnrollmentTokenResult enrolled;
        using (SandboxTelemetry.Phase("build_request", backend))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var enrollment = scope.ServiceProvider.GetRequiredService<IRunnerEnrollment>();
            enrolled = await enrollment.CreateEnrollmentTokenAsync(
                createdByUserName: request.CreatedBy ?? "sandbox-provisioner",
                machineName: name, isEphemeral: true, profile: profile);
        }

        if (enrolled.MachineId is not { } machineId)
        {
            SandboxTelemetry.RecordOutcome("error", backend);
            throw new SandboxAgentException("Enrollment did not pre-create a machine id for the sandbox.");
        }
        totalPhase.SetTag("sandbox.machineId", machineId);

        // Broker egress tunnels the dial-back through a CONNECT proxy that only carries TLS, so a brokered
        // sandbox must reach the control plane over the PUBLIC https ingress rather than a plaintext
        // in-cluster URL. That's a property of broker egress, not of any one product, so decide it here.
        string? backendOverride = null, grpcOverride = null;
        if (request.Broker is not null)
        {
            backendOverride = string.IsNullOrWhiteSpace(o.PublicBackendUrl) ? null : o.PublicBackendUrl;
            grpcOverride = string.IsNullOrWhiteSpace(o.PublicGrpcBackendUrl) ? backendOverride : o.PublicGrpcBackendUrl;
        }

        var sessionRequest = new SandboxSessionRequest
        {
            BackendUrl = backendOverride ?? o.BackendUrl ?? string.Empty,
            GrpcBackendUrl = grpcOverride
                ?? (string.IsNullOrWhiteSpace(o.GrpcBackendUrl) ? o.BackendUrl : o.GrpcBackendUrl),
            EnrollmentToken = enrolled.Token,
            Name = name,
            AddHostGateway = o.AddHostGateway,
            Repos = request.Repos,
            RepoCacheHostPath = o.RepoCacheHostPath,
            ClaudeConfigHostDir = o.ClaudeConfigHostDir,
            ClaudeConfigJsonHostFile = o.ClaudeConfigJsonHostFile,
            CodexConfigHostDir = o.CodexConfigHostDir,
            GitCredentialsHostDir = o.GitCredentialsHostDir,
            Broker = request.Broker,
            PersistentWorkspaceKey = request.PersistentWorkspaceKey,
            AdmittedTools = request.AdmittedTools,
            LimitsOverride = request.LimitsOverride,
        };

        if (configure is not null)
            sessionRequest = (configure(sessionRequest) ?? sessionRequest) with
            {
                Name = name,
                EnrollmentToken = enrolled.Token,
            };

        // Validated after the hook, because a caller may well supply the URL per-run rather than host-wide.
        if (string.IsNullOrWhiteSpace(sessionRequest.BackendUrl))
        {
            SandboxTelemetry.RecordOutcome("error", backend);
            throw new SandboxAgentException(
                $"No backend URL for the sandbox to dial. Set {SandboxAgentHostOptions.Section}:BackendUrl or " +
                "supply one from the configure hook. It must be reachable FROM INSIDE the container.");
        }

        var timeout = request.OnlineTimeout ?? TimeSpan.FromSeconds(o.OnDemandTimeoutSeconds);

        return request.HostMachineId is { } worker
            ? await ProvisionOnWorkerAsync(sessionRequest, worker, machineId, profile, backend, timeout, totalPhase, ct)
            : await ProvisionHereAsync(sessionRequest, machineId, profile, backend, timeout, totalPhase, ct);
    }

    /// <summary>Local backend (Docker or Kubernetes, per <c>Sandbox:Backend</c>).</summary>
    private async Task<ProvisionedSandbox> ProvisionHereAsync(
        SandboxSessionRequest sessionRequest, Guid machineId, string profile, string backend,
        TimeSpan timeout, SandboxTelemetry.PhaseTimer totalPhase, CancellationToken ct)
    {
        SandboxLease lease;
        try
        {
            using (SandboxTelemetry.Phase("launch", backend))
                lease = await manager.ProvisionAsync(sessionRequest, profileOverride: profile, ct: ct);
        }
        catch (SandboxRuntimeException ex)
        {
            SandboxTelemetry.RecordOutcome("error", backend);
            throw new SandboxAgentException($"The sandbox container could not be launched: {ex.Message}", inner: ex);
        }

        logger.LogInformation("Sandbox {Name} launched (machine {MachineId}); waiting up to {Timeout} to come online",
            sessionRequest.Name, machineId, timeout);

        bool online;
        SandboxStatus? exitStatus;
        using (SandboxTelemetry.Phase("wait_online", backend))
            (online, exitStatus) = await WaitOnlineAsync(
                machineId, c => manager.GetStatusAsync(lease.Handle, c), backend, timeout, ct);

        if (!online)
        {
            // Read the logs BEFORE recycling — they are the only explanation of why startup failed.
            var logs = await TryGetLogsAsync(lease, ct);
            logger.LogWarning("Sandbox {Name} did not come online (terminal state {State}, exit {Exit}); recycling it",
                sessionRequest.Name, exitStatus?.State, exitStatus?.ExitCode);
            await manager.RecycleAsync(sessionRequest.Name, CancellationToken.None);

            SandboxTelemetry.RecordOutcome("not_online", backend);
            totalPhase.SetTag("sandbox.terminal_state", exitStatus?.State);
            throw new SandboxAgentException(
                NeverOnlineMessage(sessionRequest.Name, timeout, exitStatus), logs,
                terminalState: exitStatus?.State, exitCode: exitStatus?.ExitCode);
        }

        SandboxTelemetry.RecordOutcome("online", backend);
        return new ProvisionedSandbox(machineId, sessionRequest.Name, backend,
            c => manager.RecycleAsync(sessionRequest.Name, c));
    }

    /// <summary>A connected worker's own Docker, dispatched over its control channel. <c>LaunchAsync</c> stages
    /// credentials and starts the per-session broker; the wait is ours, so both paths bind the same way.
    /// Disposing the returned session recycles container + broker + staged credentials, all on the worker.</summary>
    private async Task<ProvisionedSandbox> ProvisionOnWorkerAsync(
        SandboxSessionRequest sessionRequest, Guid worker, Guid machineId, string profile, string backend,
        TimeSpan timeout, SandboxTelemetry.PhaseTimer totalPhase, CancellationToken ct)
    {
        var remote = services.GetService<RemoteSandboxManager>()
            ?? throw new SandboxAgentException(
                "Running a sandbox on a remote worker requires the remote layer — call AddRemoteWorkers().");
        var remoteRuntime = services.GetService<RemoteDockerSandboxRuntime>();

        if (!runners.IsRunnerConnected(worker))
            throw new SandboxAgentException($"Worker {worker} is not connected — cannot host a sandbox on it.");

        // Credentials live on the WORKER (the container runs there), so default anything still unset to that
        // machine's home rather than this host's paths. Values already supplied are left alone.
        if (remoteRuntime is not null && NeedsWorkerCredentials(sessionRequest))
        {
            var home = await remoteRuntime.ProbeHomeAsync(worker, ct);
            sessionRequest = sessionRequest with
            {
                ClaudeConfigHostDir = Or(sessionRequest.ClaudeConfigHostDir, $"{home}/.claude"),
                ClaudeConfigJsonHostFile = Or(sessionRequest.ClaudeConfigJsonHostFile, $"{home}/.claude.json"),
                CodexConfigHostDir = Or(sessionRequest.CodexConfigHostDir, $"{home}/.codex"),
                GitCredentialsHostDir = Or(sessionRequest.GitCredentialsHostDir, home),
            };
        }

        RemoteSandboxSession sandbox;
        try
        {
            // Launch only — the wait is ours, so BOTH paths get the same event-driven bind, the same
            // early-exit on a container that dies during startup, and the same phase telemetry.
            using (SandboxTelemetry.Phase("launch", backend))
                sandbox = await remote.LaunchAsync(worker, machineId, sessionRequest, runners.IsRunnerConnected,
                    profile: profile, onlineTimeoutSeconds: (int)timeout.TotalSeconds,
                    waitForOnline: false, ct: ct);
        }
        catch (SandboxRuntimeException ex)
        {
            SandboxTelemetry.RecordOutcome("error", backend);
            throw new SandboxAgentException(
                $"The sandbox could not be launched on worker {worker}: {ex.Message}", inner: ex);
        }

        bool online;
        SandboxStatus? exitStatus;
        using (SandboxTelemetry.Phase("wait_online", backend))
            (online, exitStatus) = await WaitOnlineAsync(
                machineId, c => remoteRuntime is null
                    ? Task.FromResult(new SandboxStatus(SandboxState.Unknown))
                    : remoteRuntime.GetStatusAsync(worker, sandbox.Handle, c),
                backend, timeout, ct);

        if (!online)
        {
            var logs = remoteRuntime is null ? null
                : await TryGetRemoteLogsAsync(remoteRuntime, worker, sandbox.Handle, ct);
            logger.LogWarning("Sandbox {Name} on worker {Worker} did not come online (terminal state {State}, exit {Exit})",
                sessionRequest.Name, worker, exitStatus?.State, exitStatus?.ExitCode);
            await sandbox.DisposeAsync(); // container + broker + staged creds, all on the worker

            SandboxTelemetry.RecordOutcome("not_online", backend);
            totalPhase.SetTag("sandbox.terminal_state", exitStatus?.State);
            throw new SandboxAgentException(
                NeverOnlineMessage(sessionRequest.Name, timeout, exitStatus), logs,
                terminalState: exitStatus?.State, exitCode: exitStatus?.ExitCode);
        }

        SandboxTelemetry.RecordOutcome("online", backend);
        return new ProvisionedSandbox(sandbox.MachineId, sessionRequest.Name, backend,
            async _ => await sandbox.DisposeAsync());
    }

    /// <summary>
    /// Wait for the pre-created machine to come online, splitting the wait into the two sub-phases that
    /// actually explain a slow start: <c>pod_ready</c> (until the container/pod is Running — scheduling,
    /// image pull, volume mount) and <c>runner_enroll</c> (Running → connected — entrypoint clone, broker
    /// wait, control-stream connect). Event-driven off <c>RunnerConnected</c>, with a status poll alongside so
    /// a container that dies during startup ends the wait instead of burning the timeout; subscribes BEFORE
    /// re-checking presence, closing the race where it connected in between.
    /// </summary>
    private async Task<(bool Online, SandboxStatus? ExitStatus)> WaitOnlineAsync(
        Guid machineId, Func<CancellationToken, Task<SandboxStatus>> getStatus, string backend,
        TimeSpan timeout, CancellationToken ct)
    {
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        long podReadyStamp = 0;
        SandboxStatus? exitStatus = null;

        void RecordPodReadyOnce()
        {
            if (Interlocked.CompareExchange(ref podReadyStamp, System.Diagnostics.Stopwatch.GetTimestamp(), 0) == 0)
                SandboxTelemetry.RecordPhaseMs("pod_ready", backend,
                    System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds);
        }

        void OnConnected(RunnerInfo info)
        {
            if (info.MachineId == machineId)
                connected.TrySetResult(true);
        }

        runners.RunnerConnected += OnConnected;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (runners.IsRunnerConnected(machineId))
                return (true, null); // already online (warm/instant) — the split would be ~0, skip it

            deadline.CancelAfter(timeout);
            await using var registration = deadline.Token.Register(() => connected.TrySetResult(false));

            _ = PollForEarlyExitAsync();
            var online = await connected.Task;
            if (online)
            {
                var readyStamp = Interlocked.Read(ref podReadyStamp);
                SandboxTelemetry.RecordPhaseMs("runner_enroll", backend,
                    System.Diagnostics.Stopwatch.GetElapsedTime(readyStamp == 0 ? waitStart : readyStamp).TotalMilliseconds);
            }
            return (online, online ? null : exitStatus);

            async Task PollForEarlyExitAsync()
            {
                try
                {
                    while (!deadline.IsCancellationRequested)
                    {
                        var status = await getStatus(deadline.Token);
                        if (status.State is SandboxState.Running)
                            RecordPodReadyOnce();
                        if (status.State is SandboxState.Exited or SandboxState.NotFound)
                        {
                            exitStatus = status;
                            connected.TrySetResult(false);
                            return;
                        }
                        await Task.Delay(options.Value.ProvisionStatusPollMs, deadline.Token);
                    }
                }
                catch (OperationCanceledException) { /* the wait finished or the deadline passed */ }
                catch (Exception ex) { logger.LogDebug(ex, "Sandbox status poll failed for machine {MachineId}", machineId); }
            }
        }
        finally
        {
            runners.RunnerConnected -= OnConnected;
            deadline.Cancel(); // stop the poll loop
        }
    }

    /// <summary>Distinguish the two failure shapes: the container DIED during startup (its exit code is the
    /// lead) versus it simply never became ready in time (a slow pull, or a dial-back it can't complete).</summary>
    private static string NeverOnlineMessage(string name, TimeSpan timeout, SandboxStatus? exitStatus) =>
        exitStatus is { State: SandboxState.Exited }
            ? $"The sandbox '{name}' exited (exit code {exitStatus.ExitCode?.ToString() ?? "unknown"}) before its " +
              "agent runner could connect — its startup failed. This is almost always a repo clone or " +
              "git-credentials error."
            : $"The sandbox '{name}' did not become ready within {timeout} and its agent runner never connected. " +
              "This is usually an image that can't be pulled or a backend URL the container can't reach.";

    private static bool NeedsWorkerCredentials(SandboxSessionRequest r) =>
        string.IsNullOrWhiteSpace(r.ClaudeConfigHostDir) ||
        string.IsNullOrWhiteSpace(r.ClaudeConfigJsonHostFile) ||
        string.IsNullOrWhiteSpace(r.CodexConfigHostDir) ||
        string.IsNullOrWhiteSpace(r.GitCredentialsHostDir);

    private static string Or(string? supplied, string fallback) =>
        string.IsNullOrWhiteSpace(supplied) ? fallback : supplied;

    private async Task<string?> TryGetRemoteLogsAsync(
        RemoteDockerSandboxRuntime runtime, Guid worker, SandboxHandle handle, CancellationToken ct)
    {
        try { return await runtime.GetLogsAsync(worker, handle, tailLines: 40, ct); }
        catch (Exception ex) { logger.LogDebug(ex, "Could not read sandbox logs from worker {Worker}", worker); return null; }
    }

    private async Task<string?> TryGetLogsAsync(SandboxLease lease, CancellationToken ct)
    {
        try { return await manager.GetLogsAsync(lease.Handle, tailLines: 40, ct); }
        catch (Exception ex) { logger.LogDebug(ex, "Could not read sandbox logs"); return null; }
    }
}

/// <summary>What to provision: which profile, which repos, where, and how long to wait.</summary>
public sealed record SandboxProvisionRequest
{
    /// <summary>Isolation profile. Falls back to <see cref="SandboxAgentHostOptions.Profile"/>.</summary>
    public string? Profile { get; init; }

    /// <summary>Repos cloned into the sandbox (each lands at <c>/repos/&lt;name&gt;</c>).</summary>
    public IReadOnlyList<SandboxRepoSpec> Repos { get; init; } = [];

    /// <summary>Provision on this connected worker's Docker instead of the local backend.</summary>
    public Guid? HostMachineId { get; init; }

    /// <summary>
    /// Broker egress for this session: which model providers the per-session broker injects, and the tight
    /// allowlist it enforces. Per-session because it follows from the session's TOOL — one broker profile can
    /// serve tools with different egress needs. Setting this also switches the sandbox's dial-back to
    /// <see cref="SandboxAgentHostOptions.PublicBackendUrl"/> when one is configured, since broker egress only
    /// tunnels TLS. Null for non-broker sessions.
    /// </summary>
    public SandboxBrokerNeeds? Broker { get; init; }

    /// <summary>Kubernetes: back <c>/repos</c> with a per-id persistent volume (survives a pod recycle).</summary>
    public Guid? PersistentWorkspaceKey { get; init; }

    /// <summary>The agent tools this sandbox may serve. Stamped onto the sandbox so a later session can be
    /// checked against it (<see cref="SandboxAdmission"/>). Leave empty for a sandbox that serves exactly the
    /// one session it is provisioned for.</summary>
    public IReadOnlyList<string> AdmittedTools { get; init; } = [];

    /// <summary>Per-session override of the profile's cgroup limits — see
    /// <see cref="SandboxSessionRequest.LimitsOverride"/>. Null → the profile's own limits.</summary>
    public SandboxResources? LimitsOverride { get; init; }

    /// <summary>Overrides <see cref="SandboxAgentHostOptions.OnlineTimeoutSeconds"/>.</summary>
    public TimeSpan? OnlineTimeout { get; init; }

    /// <summary>Recorded on the enrollment token for audit.</summary>
    public string? CreatedBy { get; init; }
}

/// <summary>An online sandbox: the machine id to dispatch sessions to, and an explicit recycle. The caller
/// decides when it dies — nothing here disposes it for you.</summary>
public sealed class ProvisionedSandbox(Guid machineId, string name, string backend, Func<CancellationToken, Task> recycle)
{
    /// <summary>Ephemeral machine identity of the sandbox's runner (the control plane's handle for it).</summary>
    public Guid MachineId { get; } = machineId;

    /// <summary>Name of the sandbox container/pod.</summary>
    public string Name { get; } = name;

    /// <summary>Which backend launched it: <c>docker</c>, <c>kubernetes</c>, or <c>docker-nested</c>.</summary>
    public string Backend { get; } = backend;

    /// <summary>Stop and remove the sandbox (plus staged credentials and broker, on the remote path).</summary>
    public Task RecycleAsync(CancellationToken ct = default) => recycle(ct);
}
