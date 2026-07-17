using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.CommandRunner;
using Xunit;

namespace Mintokei.AgentControlPlane.Tests;

/// <summary>
/// Tests the capacity slot-book <see cref="DefaultAgentControlPlane"/> gained on top of its session registry: the
/// live/active/per-machine/per-agent counts and idle marking mirror <c>IAgentProcessStore</c> exactly,
/// and the admission surface (pending claims + machine lock) delegates to <see cref="MachineAdmissionControl"/>.
/// Driven by real (fake-handle) Claude sessions — one process handle per session — so counting sees
/// genuine <c>HasExited</c> transitions rather than a stub.
/// </summary>
public class DefaultAgentControlPlaneCapacityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static string RequestIdOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("request_id").GetString()!;
    }

    private static string ControlResponse(string requestId, string subtype)
        => JsonSerializer.Serialize(new { type = "control_response", response = new { request_id = requestId, subtype } });

    /// <summary>Mints a fresh <see cref="FakeProcessHandle"/> per <c>Create()</c> so multiple live
    /// sessions don't share one stdio pipe.</summary>
    private sealed class MultiRunnerFactory : ICommandLineRunnerFactory
    {
        private readonly List<FakeProcessHandle> _handles = new();
        private readonly object _gate = new();

        /// <summary>The handle minted for the most recent <c>Create()</c> — valid after a session start.</summary>
        public FakeProcessHandle Newest { get { lock (_gate) return _handles[^1]; } }

        public ICommandLineRunner Create(Guid? runnerMachineId)
        {
            var handle = new FakeProcessHandle();
            lock (_gate) _handles.Add(handle);
            return new Runner(handle);
        }

        private sealed class Runner(FakeProcessHandle handle) : ICommandLineRunner
        {
            public IAsyncEnumerable<CommandOutput> RunAsync(CommandLineOptions o, CancellationToken ct = default)
                => throw new NotSupportedException("The launcher uses Start(), not RunAsync().");

            public (IProcessHandle Handle, IAsyncEnumerable<CommandOutput> Output) Start(
                CommandLineOptions o, CancellationToken ct = default)
                => (handle, handle.Output);
        }
    }

    private static DefaultAgentControlPlane NewServer(MultiRunnerFactory runner, RunnerConnectionTracker tracker)
        => new(new AgentSessionLauncher([new ClaudeBackend()], runner, tracker, NullLoggerFactory.Instance), tracker);

    /// <summary>Starts a Claude session and drives its handshake to completion; returns the live
    /// session and the fake handle backing it (for killing / feeding).</summary>
    private static async Task<(IAgentSession Session, FakeProcessHandle Handle)> StartAsync(
        DefaultAgentControlPlane server, MultiRunnerFactory runner, Guid? machine = null, Guid? agentId = null)
    {
        var startTask = server.StartSessionAsync(
            new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli }, machine, agentId);

        var handle = runner.Newest;   // Create() ran synchronously inside the launcher before the handshake await
        var initLine = await handle.WaitForWriteAsync(l => l.Contains("\"subtype\":\"initialize\""), Timeout);
        handle.FeedStdout(ControlResponse(RequestIdOf(initLine), "success"));

        return (await startTask.WaitAsync(Timeout), handle);
    }

    /// <summary>Starts a session registered under a caller-supplied <paramref name="sessionKey"/>
    /// (Mintokei would pass an <c>AgentTask.Id</c>) and drives its handshake to completion.</summary>
    private static async Task<IAgentSession> StartKeyedAsync(
        DefaultAgentControlPlane server, MultiRunnerFactory runner, Guid sessionKey,
        Guid? agentId = null, Guid? machine = null)
    {
        var startTask = server.StartSessionAsync(
            sessionKey, new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli }, machine, agentId);

        var handle = runner.Newest;
        var initLine = await handle.WaitForWriteAsync(l => l.Contains("\"subtype\":\"initialize\""), Timeout);
        handle.FeedStdout(ControlResponse(RequestIdOf(initLine), "success"));

        return await startTask.WaitAsync(Timeout);
    }

    /// <summary>Mints a session directly off the launcher — i.e. created <em>outside</em> any server, the
    /// way the execution service's engine path owns its own session — for testing <c>RegisterSession</c>.</summary>
    private static async Task<IAgentSession> StartViaLauncherAsync(AgentSessionLauncher launcher, MultiRunnerFactory runner)
    {
        var startTask = launcher.StartSessionAsync(new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli });
        var handle = runner.Newest;
        var initLine = await handle.WaitForWriteAsync(l => l.Contains("\"subtype\":\"initialize\""), Timeout);
        handle.FeedStdout(ControlResponse(RequestIdOf(initLine), "success"));
        return await startTask.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Counts_reflect_running_sessions_by_machine_tool_and_agent()
    {
        var runner = new MultiRunnerFactory();
        var tracker = new RunnerConnectionTracker();
        var server = NewServer(runner, tracker);

        var machine = Guid.NewGuid();
        var agent = Guid.NewGuid();
        tracker.Register(machine, "conn-1");   // the launcher rejects a spawn on a disconnected runner

        await StartAsync(server, runner);                                   // local, agentless
        await StartAsync(server, runner, machine, agent);                   // remote, agent
        await StartAsync(server, runner, machine, agentId: null);           // remote, agentless

        Assert.Equal(3, server.LiveSessionCount);
        Assert.Equal(3, server.CountByToolKey(AgentToolKey.ClaudeCodeCli));
        Assert.Equal(2, server.CountByMachine(machine));
        Assert.Equal(1, server.CountByMachineAndAgent(machine, agent));
        Assert.Equal(0, server.CountByMachine(Guid.NewGuid()));             // unknown machine
    }

    [Fact]
    public async Task Exited_sessions_drop_out_of_live_counts_but_linger_in_the_snapshot()
    {
        var runner = new MultiRunnerFactory();
        var server = NewServer(runner, new RunnerConnectionTracker());

        var (session, handle) = await StartAsync(server, runner);
        Assert.Equal(1, server.LiveSessionCount);

        handle.Kill();   // process dies on its own (no StopSessionAsync)

        Assert.Equal(0, server.LiveSessionCount);
        Assert.Equal(0, server.CountByToolKey(AgentToolKey.ClaudeCodeCli));

        // Still registered until teardown, but flagged exited — matches the store's linger-until-Remove.
        var slot = Assert.Single(server.GetSlots());
        Assert.Equal(session.SessionId, slot.SessionId);
        Assert.True(slot.HasExited);
    }

    [Fact]
    public async Task Idle_sessions_stay_live_but_are_not_active()
    {
        var runner = new MultiRunnerFactory();
        var tracker = new RunnerConnectionTracker();
        var server = NewServer(runner, tracker);

        var machine = Guid.NewGuid();
        tracker.Register(machine, "conn-1");
        var (session, _) = await StartAsync(server, runner, machine);

        Assert.Equal(1, server.CountByMachine(machine));
        Assert.Equal(1, server.CountActiveByMachine(machine));

        server.SetIdleSince(session.SessionId, DateTimeOffset.UtcNow);

        Assert.Equal(1, server.CountByMachine(machine));         // an idle CLI still holds the slot
        Assert.Equal(0, server.CountActiveByMachine(machine));   // but it's evictable, so not "active"

        server.ClearIdleSince(session.SessionId);
        Assert.Equal(1, server.CountActiveByMachine(machine));   // processing again
    }

    [Fact]
    public async Task GetSlots_projects_machine_agent_tool_and_idle_state()
    {
        var runner = new MultiRunnerFactory();
        var tracker = new RunnerConnectionTracker();
        var server = NewServer(runner, tracker);

        var machine = Guid.NewGuid();
        var agent = Guid.NewGuid();
        tracker.Register(machine, "conn-1");
        var idleAt = DateTimeOffset.UtcNow;

        var (session, _) = await StartAsync(server, runner, machine, agent);
        server.SetIdleSince(session.SessionId, idleAt);

        var slot = Assert.Single(server.GetSlots());
        Assert.Equal(session.SessionId, slot.SessionId);
        Assert.Equal(machine, slot.RunnerMachineId);
        Assert.Equal(agent, slot.AgentId);
        Assert.Equal(AgentToolKey.ClaudeCodeCli, slot.Tool);
        Assert.Equal(idleAt, slot.IdleSince);
        Assert.False(slot.HasExited);
    }

    [Fact]
    public async Task StopSessionAsync_frees_the_slot()
    {
        var runner = new MultiRunnerFactory();
        var server = NewServer(runner, new RunnerConnectionTracker());

        var (session, _) = await StartAsync(server, runner);
        Assert.Equal(1, server.LiveSessionCount);

        Assert.True(await server.StopSessionAsync(session.SessionId));

        Assert.Equal(0, server.LiveSessionCount);
        Assert.Empty(server.GetSlots());
    }

    [Fact]
    public void Admission_surface_delegates_pending_claims_and_the_machine_lock()
    {
        var server = NewServer(new MultiRunnerFactory(), new RunnerConnectionTracker());
        var machine = Guid.NewGuid();

        var claim = server.AddPendingClaim(machine, agentId: null);
        Assert.Equal(1, server.GetPendingClaimsByMachine(machine));

        claim.Dispose();
        Assert.Equal(0, server.GetPendingClaimsByMachine(machine));

        Assert.Same(server.GetMachineLock(machine), server.GetMachineLock(machine));
    }

    [Fact]
    public async Task Keyed_sessions_are_registered_under_the_caller_key_and_slots_expose_it()
    {
        var runner = new MultiRunnerFactory();
        var tracker = new RunnerConnectionTracker();
        var server = NewServer(runner, tracker);

        var sessionKey = Guid.NewGuid();   // e.g. an AgentTask.Id — opaque to the control plane
        var agent = Guid.NewGuid();
        var machine = Guid.NewGuid();
        tracker.Register(machine, "conn-1");

        var session = await StartKeyedAsync(server, runner, sessionKey, agent, machine);

        // Reachable by the caller-supplied key — not by the engine's internal session id.
        Assert.Same(session, server.GetSession(sessionKey));
        Assert.Null(server.GetSession(session.SessionId));

        // Idle marking and teardown are driven by the same key.
        server.SetIdleSince(sessionKey, DateTimeOffset.UtcNow);
        Assert.Equal(0, server.CountActiveByMachine(machine));

        var slot = Assert.Single(server.GetSlots());
        Assert.Equal(sessionKey, slot.Key);               // the caller's key (eviction identity)
        Assert.Equal(session.SessionId, slot.SessionId);  // the engine's own id — distinct
        Assert.Equal(machine, slot.RunnerMachineId);
        Assert.Equal(agent, slot.AgentId);

        Assert.True(await server.StopSessionAsync(sessionKey));   // stop by the caller key
        Assert.Empty(server.GetSlots());
    }

    [Fact]
    public async Task SessionStarted_event_carries_the_caller_key()
    {
        var runner = new MultiRunnerFactory();
        var server = NewServer(runner, new RunnerConnectionTracker());

        AgentSessionInfo? started = null;
        server.SessionStarted += i => started = i;

        var sessionKey = Guid.NewGuid();
        await StartKeyedAsync(server, runner, sessionKey);

        Assert.NotNull(started);
        Assert.Equal(sessionKey, started!.Key);   // the sidecar (Stage E) correlates on this
    }

    [Fact]
    public async Task RegisterSession_mirrors_an_externally_created_session_and_Deregister_is_identity_guarded()
    {
        var runner = new MultiRunnerFactory();
        var tracker = new RunnerConnectionTracker();
        var launcher = new AgentSessionLauncher([new ClaudeBackend()], runner, tracker, NullLoggerFactory.Instance);
        var server = new DefaultAgentControlPlane(launcher, tracker);   // shares the launcher so we can mint sessions outside it

        var key = Guid.NewGuid();       // Mintokei's AgentTask.Id
        var machine = Guid.NewGuid();
        var agent = Guid.NewGuid();

        // The execution service's engine path owns its session; mirror it into the registry (the bridge).
        var session = await StartViaLauncherAsync(launcher, runner);
        server.RegisterSession(key, session, AgentToolKey.ClaudeCodeCli, machine, agent);

        Assert.Same(session, server.GetSession(key));
        Assert.Equal(1, server.CountByMachine(machine));
        var slot = Assert.Single(server.GetSlots());
        Assert.Equal(key, slot.Key);
        Assert.Equal(machine, slot.RunnerMachineId);
        Assert.Equal(agent, slot.AgentId);

        // A mid-turn respawn re-registers a new session under the same key…
        var respawned = await StartViaLauncherAsync(launcher, runner);
        server.RegisterSession(key, respawned, AgentToolKey.ClaudeCodeCli, machine, agent);

        // …so deregistering the *stale* session must be a guarded no-op — the current one survives.
        Assert.False(server.DeregisterSession(key, session));
        Assert.Same(respawned, server.GetSession(key));

        // Deregistering the current session removes it and fires SessionEnded.
        AgentSessionInfo? ended = null;
        server.SessionEnded += i => ended = i;
        Assert.True(server.DeregisterSession(key, respawned));
        Assert.Null(server.GetSession(key));
        Assert.Empty(server.GetSlots());
        Assert.Equal(key, ended!.Key);
    }
}
