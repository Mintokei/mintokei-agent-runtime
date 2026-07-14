using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine;

/// <summary>
/// Spawns <see cref="IAgentSession"/> instances. The only infrastructure a session needs is a way
/// to start a process (<see cref="ICommandLineRunnerFactory"/>) and a logger — no DB, no message
/// stream, no event bus, no process store.
///
/// Register with <c>services.AddSingleton&lt;AgentSessionFactory&gt;()</c> to use it.
/// </summary>
public sealed class AgentSessionFactory
{
    private readonly ICommandLineRunnerFactory _runnerFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ClaudeBackend _claude = new();

    public AgentSessionFactory(ICommandLineRunnerFactory runnerFactory, ILoggerFactory loggerFactory)
    {
        _runnerFactory = runnerFactory;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Spawns a session for any <paramref name="backend"/> (Claude/Codex/ACP), runs its handshake, and
    /// returns it ready for <see cref="IAgentSession.SendMessageAsync"/>.
    ///
    /// Where the CLI runs is decided entirely by the injected <see cref="ICommandLineRunnerFactory"/>:
    /// <paramref name="runnerMachineId"/> = <c>null</c> runs it on the current machine; a machine id
    /// runs it on a <em>remote</em> runner — provided the host registered a remote-capable factory
    /// (e.g. <c>Mintokei.Runner.Host</c>'s, which dispatches over the durable outbox + gRPC). The
    /// default <see cref="LocalCommandLineRunnerFactory"/> ignores the id and always runs local, so a
    /// non-null id is harmless there. The engine itself stays transport-agnostic — it only forwards the
    /// opaque id to the factory.
    /// </summary>
    public async Task<IAgentSession> CreateSessionAsync(
        IAgentBackend backend,
        AgentSessionSpec spec,
        Guid? runnerMachineId = null,
        AgentSessionOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new AgentSessionOptions();

        var runner = _runnerFactory.Create(runnerMachineId); // null → local; machine id → remote (if a remote factory is registered)
        var cts = new CancellationTokenSource();
        var (handle, output) = runner.Start(backend.BuildCommandLine(spec), cts.Token);

        var sessionId = Guid.NewGuid();
        var logger = _loggerFactory.CreateLogger($"AgentSession:{sessionId}");
        var session = new AgentSession(
            sessionId,
            handle,
            output,
            backend.CreateProtocol(spec, logger),
            backend.ReplyBuilder,
            options,
            cts,
            logger,
            initialAgentSessionId: spec.ResumeSessionId);

        await session.StartAsync(resume: !string.IsNullOrEmpty(spec.ResumeSessionId), ct);
        return session;
    }

    /// <summary>Spawns a Claude Code session (via <see cref="ClaudeBackend"/> — the same launch builder
    /// prod uses), runs its handshake, and returns it ready for
    /// <see cref="IAgentSession.SendMessageAsync"/>. Pass <paramref name="runnerMachineId"/> to run the
    /// CLI on a remote runner (null runs local) — a thin wrapper over
    /// <see cref="CreateSessionAsync"/>.</summary>
    public Task<IAgentSession> CreateClaudeSessionAsync(
        AgentSessionSpec spec,
        Guid? runnerMachineId = null,
        AgentSessionOptions? options = null,
        CancellationToken ct = default)
        => CreateSessionAsync(_claude, spec, runnerMachineId, options, ct);

    /// <summary>
    /// Wraps a Claude session around a process the orchestrator already spawned (handle + output +
    /// cts), WITHOUT spawning or starting it — the caller starts the Output consumer first, then
    /// calls <see cref="IAgentSession.StartAsync"/>. This is the prod-integration path: remote-runner
    /// spawn, retry, and process lifecycle stay with the execution service; the session owns only the
    /// protocol. <paramref name="sessionId"/> is the agent-task id so the parser tags messages with it.
    /// </summary>
    public AgentSession WrapClaude(
        Guid sessionId,
        IProcessHandle handle,
        IAsyncEnumerable<CommandOutput> output,
        CancellationTokenSource cts,
        string? resumeSessionId,
        AgentSessionOptions? options = null)
    {
        var logger = _loggerFactory.CreateLogger($"AgentSession:{sessionId}");
        return new AgentSession(
            sessionId,
            handle,
            output,
            new ClaudeSessionProtocol(logger),
            new ClaudeInteractionReplyBuilder(),
            options ?? new AgentSessionOptions(),
            cts,
            logger,
            initialAgentSessionId: resumeSessionId);
    }

    /// <summary>
    /// Wraps a session around a process the orchestrator already spawned, using any backend's module
    /// for the protocol + reply builder (Codex/ACP prod integration). Like <see cref="WrapClaude"/>
    /// but the protocol comes from <paramref name="backend"/> built over <paramref name="spec"/>. The
    /// caller starts the Output consumer first, then <see cref="IAgentSession.StartAsync"/>.
    /// </summary>
    public AgentSession Wrap(
        IAgentBackend backend,
        AgentSessionSpec spec,
        Guid sessionId,
        IProcessHandle handle,
        IAsyncEnumerable<CommandOutput> output,
        CancellationTokenSource cts,
        AgentSessionOptions? options = null)
    {
        var logger = _loggerFactory.CreateLogger($"AgentSession:{sessionId}");
        return new AgentSession(
            sessionId,
            handle,
            output,
            backend.CreateProtocol(spec, logger),
            backend.ReplyBuilder,
            options ?? new AgentSessionOptions(),
            cts,
            logger,
            initialAgentSessionId: spec.ResumeSessionId);
    }
}
