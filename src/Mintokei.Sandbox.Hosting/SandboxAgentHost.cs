using Microsoft.Extensions.Logging;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine;

namespace Mintokei.Sandbox.Hosting;

/// <summary>
/// Runs an agent session inside a fresh sandbox, in one call: provision (via
/// <see cref="SandboxProvisioner"/>) → dispatch the session onto it → stream it → recycle on dispose.
///
/// <para>This is the <em>scoped</em> shape — the sandbox lives exactly as long as the run. A product that
/// keeps one sandbox pinned across many turns and reaps it on its own schedule should use
/// <see cref="SandboxProvisioner"/> directly and own the lifetime itself.</para>
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
    SandboxProvisioner provisioner,
    IAgentControlPlane plane,
    ILogger<SandboxAgentHost> logger)
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
    /// Last-word hook over the composed <see cref="SandboxSessionRequest"/>, for everything that is per-run
    /// rather than host-wide: broker egress needs, per-tenant credential paths, a machine-local repo mirror,
    /// a different backend URL for this session. See <see cref="SandboxProvisioner.ProvisionAsync"/>.
    /// </param>
    /// <param name="ct">Cancellation. The sandbox is recycled if the run is cancelled after launching.</param>
    /// <exception cref="SandboxAgentException">The sandbox could not be launched, never came online, or the
    /// session could not start. The sandbox is always recycled before this throws.</exception>
    public async Task<SandboxAgentRun> RunAsync(
        SandboxAgentRequest request,
        Func<SandboxSessionRequest, SandboxSessionRequest>? configure,
        CancellationToken ct = default)
    {
        var sandbox = await provisioner.ProvisionAsync(
            new SandboxProvisionRequest
            {
                Profile = request.Profile,
                Repos = request.AllRepos(),
                HostMachineId = request.HostMachineId,
                PersistentWorkspaceTaskId = request.PersistentWorkspaceTaskId,
                OnlineTimeout = request.OnlineTimeout,
                CreatedBy = "sandbox-agent-host",
            },
            configure, ct);

        try
        {
            return await StartSessionAsync(request, sandbox, ct);
        }
        catch
        {
            await sandbox.RecycleAsync(CancellationToken.None); // never leak the container
            throw;
        }
    }

    /// <summary>Dispatch the session onto the (now online) sandbox and wrap it in a run that cleans up.</summary>
    private async Task<SandboxAgentRun> StartSessionAsync(
        SandboxAgentRequest request, ProvisionedSandbox sandbox, CancellationToken ct)
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
                request.SessionKey, spec, runnerMachineId: sandbox.MachineId,
                options: request.SessionOptions, ct: ct);
        }
        catch (Exception ex)
        {
            throw new SandboxAgentException(
                $"The sandbox '{sandbox.Name}' came online but the {request.Tool} session could not start: " +
                $"{ex.Message}. Check that this backend is registered on the host (e.g. .AddClaude()) and that " +
                "the CLI is present in the sandbox image.", inner: ex);
        }

        var stopKey = request.SessionKey ?? session.SessionId;
        var run = new SandboxAgentRun(session, sandbox.MachineId, sandbox.Name, async () =>
        {
            try { await plane.StopSessionAsync(stopKey); }
            catch (Exception ex) { logger.LogWarning(ex, "Stopping session for sandbox {Name} failed", sandbox.Name); }
            finally { await sandbox.RecycleAsync(CancellationToken.None); }
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
}
