using System.Linq;
using Microsoft.Extensions.Logging;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.AgentEngine.AgentTools;

namespace Mintokei.AgentControlPlane;

/// <summary>
/// DB-free launcher: lists connected runners and starts an <see cref="IAgentSession"/> on any of them
/// — local or remote — through <em>one</em> code path, since
/// <see cref="ICommandLineRunnerFactory"/> already resolves local-vs-remote. No DbContext, no
/// AgentTask, no auth service: the caller hands a fully-populated <see cref="AgentSessionSpec"/> and
/// picks the machine. Internal for now because <see cref="IAgentSession.Output"/> still exposes the
/// runtime's stream vocabulary directly.
/// </summary>
internal sealed class AgentSessionLauncher
{
    private readonly IReadOnlyDictionary<AgentToolKey, IAgentBackend> _backends;
    private readonly ICommandLineRunnerFactory _runnerFactory;
    private readonly RunnerConnectionTracker _runners;
    private readonly ILoggerFactory _logging;

    public AgentSessionLauncher(
        IEnumerable<IAgentBackend> backends,
        ICommandLineRunnerFactory runnerFactory,
        RunnerConnectionTracker runners,
        ILoggerFactory logging)
    {
        _backends = backends.ToDictionary(b => b.Tool);
        _runnerFactory = runnerFactory;
        _runners = runners;
        _logging = logging;
    }

    /// <summary>The runners connected right now (live registry — no DB).</summary>
    public IReadOnlyList<Guid> ListConnectedRunners() => [.. _runners.ConnectedMachineIds];

    public bool IsRunnerConnected(Guid machineId) => _runners.IsConnected(machineId);

    /// <summary>
    /// Spawns a session for <paramref name="spec"/> — locally when <paramref name="runnerMachineId"/>
    /// is null, otherwise on that connected runner — runs its handshake, and returns it ready to use.
    /// </summary>
    public async Task<IAgentSession> StartSessionAsync(
        AgentSessionSpec spec, Guid? runnerMachineId = null,
        AgentSessionOptions? options = null, CancellationToken ct = default)
    {
        if (runnerMachineId is { } machineId && !_runners.IsConnected(machineId))
            throw new InvalidOperationException($"Runner {machineId} is not connected.");

        if (!_backends.TryGetValue(spec.Tool, out var backend))
            throw new InvalidOperationException($"No session backend registered for {spec.Tool}.");

        var sessionId = Guid.NewGuid();
        var logger = _logging.CreateLogger($"AgentSession:{spec.Tool}:{sessionId}");

        var runner = _runnerFactory.Create(runnerMachineId);   // local | RemoteCommandLineRunner
        var cts = new CancellationTokenSource();
        var (handle, output) = runner.Start(backend.BuildCommandLine(spec), cts.Token);

        var session = new AgentSession(
            sessionId, handle, output,
            backend.CreateProtocol(spec, logger), backend.ReplyBuilder,
            options ?? new AgentSessionOptions(), cts, logger,
            initialAgentSessionId: spec.ResumeSessionId);

        await session.StartAsync(resume: !string.IsNullOrEmpty(spec.ResumeSessionId), ct);
        return session;
    }
}
