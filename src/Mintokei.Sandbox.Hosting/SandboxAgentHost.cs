using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine;
using Mintokei.Runner.Host.Server;
using Mintokei.Sandbox.Docker;

namespace Mintokei.Sandbox.Hosting;

/// <summary>
/// Runs an agent session inside a fresh sandbox, in one call.
///
/// <para>Bringing a sandboxed agent up is always the same six steps — mint a one-time enrollment token that
/// pre-creates the machine identity, launch the container, wait for the runner inside it to dial back and
/// connect, dispatch the session onto that machine, stream it, then recycle everything. This type owns those
/// steps so callers don't re-implement them, and so the tricky parts (binding by id instead of racing on a
/// name, noticing a container that exits during startup, cleaning up on every failure path) are written once.</para>
///
/// <para>The backend is a deployment choice, not a code choice: the same call runs the sandbox on local
/// Docker or in Kubernetes depending on <c>Sandbox:Backend</c>, or on a connected remote worker when the
/// request names one via <see cref="SandboxAgentRequest.HostMachineId"/>.</para>
/// </summary>
/// <example>
/// <code>
/// await using var run = await host.RunAsync(new SandboxAgentRequest
/// {
///     Tool = AgentToolKey.ClaudeCodeCli,
///     Repo = "https://github.com/acme/app",
///     Prompt = "add a test for the parser",
/// });
/// var turn = await run.CollectTurnAsync();
/// Console.WriteLine(turn.Transcript);
/// </code>
/// </example>
public sealed class SandboxAgentHost(
    IRunnerEnrollment enrollment,
    SandboxManager manager,
    IAgentControlPlane plane,
    IOptions<SandboxAgentHostOptions> options,
    ILogger<SandboxAgentHost> logger,
    IServiceProvider services)
{
    /// <summary>
    /// Provision a sandbox, wait for it to come online, and start an agent session inside it. Sends
    /// <see cref="SandboxAgentRequest.Prompt"/> when set. The returned run owns the sandbox — dispose it.
    /// </summary>
    /// <exception cref="SandboxAgentException">The sandbox could not be launched, never came online, or the
    /// session could not start. The sandbox is always recycled before this throws.</exception>
    public Task<SandboxAgentRun> RunAsync(SandboxAgentRequest request, CancellationToken ct = default) =>
        RunAsync(request, configure: null, ct);

    /// <summary>
    /// Provision a sandbox, wait for it to come online, and start an agent session inside it, shaping the
    /// sandbox spec per-run via <paramref name="configure"/>. The returned run owns the sandbox — dispose it.
    /// </summary>
    /// <param name="request">What to run and where to isolate it.</param>
    /// <param name="configure">
    /// Optional last-word hook over the composed <see cref="SandboxSessionRequest"/>, for everything that is
    /// per-run rather than host-wide: broker egress needs (<see cref="SandboxSessionRequest.Broker"/>),
    /// per-tenant credential paths, a machine-local repo mirror, a different backend URL for this session.
    /// The host-wide options are applied first, so a hook only overrides what it names.
    /// <para>The sandbox's <c>Name</c> and <c>EnrollmentToken</c> are re-pinned afterwards: they are the
    /// identity this run is bound to, not policy, and changing them would break the binding.</para>
    /// </param>
    /// <param name="ct">Cancellation. The sandbox is recycled if the run is cancelled after launching.</param>
    /// <exception cref="SandboxAgentException">The sandbox could not be launched, never came online, or the
    /// session could not start. The sandbox is always recycled before this throws.</exception>
    public async Task<SandboxAgentRun> RunAsync(
        SandboxAgentRequest request,
        Func<SandboxSessionRequest, SandboxSessionRequest>? configure,
        CancellationToken ct = default)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.BackendUrl))
            throw new SandboxAgentException(
                $"{SandboxAgentHostOptions.Section}:BackendUrl is not configured. Set it to a URL the sandbox " +
                "can reach FROM INSIDE the container (not localhost unless it shares the host's network).");

        var profile = request.Profile ?? o.Profile;
        var name = $"sandbox-{profile}-{Guid.NewGuid().ToString("N")[..12]}";

        // 1. Mint the identity FIRST. Pre-creating the machine id means we bind the session to a known id
        //    rather than discovering the runner by name after it enrolls — no races, no ambiguity when
        //    several sandboxes start at once.
        var enrolled = await enrollment.CreateEnrollmentTokenAsync(
            createdByUserName: "sandbox-agent-host", machineName: name, isEphemeral: true, profile: profile);
        if (enrolled.MachineId is not { } machineId)
            throw new SandboxAgentException("Enrollment did not pre-create a machine id for the sandbox.");

        var repos = request.AllRepos();
        var sessionRequest = new SandboxSessionRequest
        {
            BackendUrl = o.BackendUrl,
            GrpcBackendUrl = string.IsNullOrWhiteSpace(o.GrpcBackendUrl) ? o.BackendUrl : o.GrpcBackendUrl,
            EnrollmentToken = enrolled.Token,
            Name = name,
            AddHostGateway = o.AddHostGateway,
            Repos = repos,
            RepoCacheHostPath = o.RepoCacheHostPath,
            ClaudeConfigHostDir = o.ClaudeConfigHostDir,
            ClaudeConfigJsonHostFile = o.ClaudeConfigJsonHostFile,
            CodexConfigHostDir = o.CodexConfigHostDir,
            GitCredentialsHostDir = o.GitCredentialsHostDir,
            PersistentWorkspaceTaskId = request.PersistentWorkspaceTaskId,
        };

        // Per-run overrides win over the host-wide defaults — then re-pin the identity fields, so a hook
        // can shape everything about the session without being able to break what it is bound to.
        if (configure is not null)
            sessionRequest = (configure(sessionRequest) ?? sessionRequest) with
            {
                Name = name,
                EnrollmentToken = enrolled.Token,
            };

        var timeout = request.OnlineTimeout ?? TimeSpan.FromSeconds(o.OnlineTimeoutSeconds);

        return request.HostMachineId is { } worker
            ? await RunOnWorkerAsync(request, sessionRequest, worker, machineId, profile, timeout, ct)
            : await RunHereAsync(request, sessionRequest, machineId, profile, timeout, ct);
    }

    /// <summary>Local backend (Docker or Kubernetes, per <c>Sandbox:Backend</c>): provision here, wait, dispatch.</summary>
    private async Task<SandboxAgentRun> RunHereAsync(
        SandboxAgentRequest request, SandboxSessionRequest sessionRequest,
        Guid machineId, string profile, TimeSpan timeout, CancellationToken ct)
    {
        SandboxLease lease;
        try
        {
            lease = await manager.ProvisionAsync(sessionRequest, profileOverride: profile, ct: ct);
        }
        catch (SandboxRuntimeException ex)
        {
            throw new SandboxAgentException($"The sandbox container could not be launched: {ex.Message}", inner: ex);
        }

        logger.LogInformation("Sandbox {Name} launched (machine {MachineId}); waiting up to {Timeout} to come online",
            sessionRequest.Name, machineId, timeout);

        try
        {
            // Bounded wait for the in-container runner. Bails early if the container exits first — a startup
            // failure (bad credentials, failed clone) shouldn't cost the full timeout.
            if (!await WaitOnlineAsync(machineId, c => manager.GetStatusAsync(lease.Handle, c), timeout, ct))
                throw new SandboxAgentException(
                    $"Sandbox '{sessionRequest.Name}' never came online within {timeout}. This is usually a failed " +
                    "repo clone, missing git credentials, an image that can't be pulled, or a backend URL the " +
                    "container can't reach.",
                    await TryGetLogsAsync(lease, ct));

            return await StartSessionAsync(request, sessionRequest.Name, machineId,
                cleanup: async () => await manager.RecycleAsync(sessionRequest.Name, CancellationToken.None), ct);
        }
        catch
        {
            await manager.RecycleAsync(sessionRequest.Name, CancellationToken.None); // never leak the container
            throw;
        }
    }

    /// <summary>Remote worker: the container is launched on that worker's Docker over its control channel.
    /// <c>LaunchAsync</c> already stages credentials and waits for the runner, and the returned session
    /// recycles the container + staged credentials + broker on dispose.</summary>
    private async Task<SandboxAgentRun> RunOnWorkerAsync(
        SandboxAgentRequest request, SandboxSessionRequest sessionRequest,
        Guid worker, Guid machineId, string profile, TimeSpan timeout, CancellationToken ct)
    {
        var remote = services.GetService<RemoteSandboxManager>()
            ?? throw new SandboxAgentException(
                "Running a sandbox on a remote worker requires the remote layer. Call AddRemoteWorkers() " +
                "on the sandbox-agent-host builder.");

        if (!plane.IsRunnerConnected(worker))
            throw new SandboxAgentException($"Worker {worker} is not connected — cannot host a sandbox on it.");

        // Credentials live on the WORKER (the container runs there), so default anything still unset to that
        // machine's home rather than this host's paths. Values already supplied — by options or by the
        // per-run hook — are left alone.
        if (services.GetService<RemoteDockerSandboxRuntime>() is { } runtime && NeedsWorkerCredentials(sessionRequest))
        {
            var home = await runtime.ProbeHomeAsync(worker, ct);
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
            sandbox = await remote.LaunchAsync(worker, machineId, sessionRequest, plane.IsRunnerConnected,
                profile: profile, onlineTimeoutSeconds: (int)timeout.TotalSeconds, ct: ct);
        }
        catch (SandboxRuntimeException ex)
        {
            throw new SandboxAgentException(
                $"The sandbox could not be launched on worker {worker}: {ex.Message}", inner: ex);
        }

        try
        {
            return await StartSessionAsync(request, sessionRequest.Name, sandbox.MachineId,
                cleanup: async () => await sandbox.DisposeAsync(), ct);
        }
        catch
        {
            await sandbox.DisposeAsync();
            throw;
        }
    }

    /// <summary>Dispatch the session onto the (now online) sandbox and wrap it in a run that cleans up.</summary>
    private async Task<SandboxAgentRun> StartSessionAsync(
        SandboxAgentRequest request, string name, Guid machineId, Func<ValueTask> cleanup, CancellationToken ct)
    {
        // Default to the first repo's checkout so the agent starts inside the code it was given.
        var repos = request.AllRepos();
        var workingDirectory = request.WorkingDirectory
            ?? (repos.Count > 0
                ? repos[0].SourcePath ?? SandboxSpecFactory.DefaultSourcePath(repos[0].Url)
                : SandboxSpecFactory.RepoRoot);

        var spec = new AgentSessionSpec { Tool = request.Tool, WorkingDirectory = workingDirectory };

        IAgentSession session;
        try
        {
            session = await plane.StartSessionAsync(
                request.SessionKey, spec, runnerMachineId: machineId,
                options: request.SessionOptions, ct: ct);
        }
        catch (Exception ex)
        {
            throw new SandboxAgentException(
                $"The sandbox '{name}' came online but the {request.Tool} session could not start: {ex.Message}. " +
                "Check that this backend is registered on the host (e.g. .AddClaude()) and that the CLI is " +
                "present in the sandbox image.", inner: ex);
        }

        var stopKey = request.SessionKey ?? session.SessionId;
        var run = new SandboxAgentRun(session, machineId, name, async () =>
        {
            try { await plane.StopSessionAsync(stopKey); }
            catch (Exception ex) { logger.LogWarning(ex, "Stopping session for sandbox {Name} failed", name); }
            finally { await cleanup(); }
        });

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            try
            {
                await session.SendMessageAsync(request.Prompt, ct);
            }
            catch
            {
                await run.DisposeAsync();
                throw;
            }
        }

        return run;
    }

    /// <summary>
    /// Wait for the pre-created machine to come online. Event-driven (the control plane raises
    /// <c>RunnerConnected</c> when the sandbox's runner connects) with a status poll alongside it, so a
    /// container that dies during startup ends the wait immediately instead of burning the timeout.
    /// Subscribes BEFORE re-checking presence, which closes the race where it connected in between.
    /// </summary>
    private async Task<bool> WaitOnlineAsync(
        Guid machineId, Func<CancellationToken, Task<SandboxStatus>> getStatus, TimeSpan timeout, CancellationToken ct)
    {
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnConnected(RunnerInfo info)
        {
            if (info.MachineId == machineId)
                connected.TrySetResult(true);
        }

        plane.RunnerConnected += OnConnected;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (plane.IsRunnerConnected(machineId))
                return true;

            deadline.CancelAfter(timeout);
            await using var registration = deadline.Token.Register(() => connected.TrySetResult(false));

            _ = PollForEarlyExitAsync();
            return await connected.Task;

            async Task PollForEarlyExitAsync()
            {
                try
                {
                    while (!deadline.IsCancellationRequested)
                    {
                        var status = await getStatus(deadline.Token);
                        if (status.State is SandboxState.Exited or SandboxState.NotFound)
                        {
                            connected.TrySetResult(false);
                            return;
                        }
                        await Task.Delay(options.Value.StatusPollMilliseconds, deadline.Token);
                    }
                }
                catch (OperationCanceledException) { /* the wait finished or the deadline passed */ }
                catch (Exception ex) { logger.LogDebug(ex, "Sandbox status poll failed for machine {MachineId}", machineId); }
            }
        }
        finally
        {
            plane.RunnerConnected -= OnConnected;
            deadline.Cancel(); // stop the poll loop
        }
    }

    /// <summary>True when any credential path is still unset, so the worker's own home is worth probing.</summary>
    private static bool NeedsWorkerCredentials(SandboxSessionRequest r) =>
        string.IsNullOrWhiteSpace(r.ClaudeConfigHostDir) ||
        string.IsNullOrWhiteSpace(r.ClaudeConfigJsonHostFile) ||
        string.IsNullOrWhiteSpace(r.CodexConfigHostDir) ||
        string.IsNullOrWhiteSpace(r.GitCredentialsHostDir);

    private static string Or(string? supplied, string fallback) =>
        string.IsNullOrWhiteSpace(supplied) ? fallback : supplied;

    private async Task<string?> TryGetLogsAsync(SandboxLease lease, CancellationToken ct)
    {
        try { return await manager.GetLogsAsync(lease.Handle, tailLines: 40, ct); }
        catch (Exception ex) { logger.LogDebug(ex, "Could not read sandbox logs"); return null; }
    }
}
