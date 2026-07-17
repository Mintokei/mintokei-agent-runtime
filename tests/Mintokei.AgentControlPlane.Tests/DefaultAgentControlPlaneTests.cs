using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Xunit;

namespace Mintokei.AgentControlPlane.Tests;

/// <summary>
/// Tests for the additive DB-free <see cref="DefaultAgentControlPlane"/> control-plane facade — driven by a fake
/// runner factory (no CLI). Covers runner listing (delegated to the tracker) and the session registry
/// lifecycle: start registers + raises SessionStarted; stop deregisters + raises SessionEnded.
/// </summary>
public class DefaultAgentControlPlaneTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static DefaultAgentControlPlane NewServer(FakeCommandLineRunnerFactory runner, RunnerConnectionTracker? tracker = null)
    {
        tracker ??= new RunnerConnectionTracker();
        return new DefaultAgentControlPlane(
            new AgentSessionLauncher([new ClaudeBackend()], runner, tracker, NullLoggerFactory.Instance),
            tracker);
    }

    private static string RequestIdOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("request_id").GetString()!;
    }

    private static string ControlResponse(string requestId, string subtype)
        => JsonSerializer.Serialize(new { type = "control_response", response = new { request_id = requestId, subtype } });

    private static async Task<(DefaultAgentControlPlane Server, IAgentSession Session)> StartClaudeAsync(FakeCommandLineRunnerFactory runner, DefaultAgentControlPlane server)
    {
        var startTask = server.StartSessionAsync(new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli });
        var initLine = await runner.Handle.WaitForWriteAsync(l => l.Contains("\"subtype\":\"initialize\""), Timeout);
        runner.Handle.FeedStdout(ControlResponse(RequestIdOf(initLine), "success"));
        return (server, await startTask.WaitAsync(Timeout));
    }

    [Fact]
    public void ListRunners_reflects_the_tracker()
    {
        var tracker = new RunnerConnectionTracker();
        var server = NewServer(new FakeCommandLineRunnerFactory(), tracker);

        Assert.Empty(server.ListRunners());

        var machine = Guid.NewGuid();
        tracker.Register(machine, "conn-1");

        Assert.Contains(server.ListRunners(), r => r.MachineId == machine && r.ConnectionId == "conn-1");
        Assert.True(server.IsRunnerConnected(machine));
    }

    [Fact]
    public void ConnectRunner_registers_and_raises_events_then_DisconnectRunner_removes_it()
    {
        var server = NewServer(new FakeCommandLineRunnerFactory());

        RunnerInfo? connected = null;
        RunnerInfo? disconnected = null;
        server.RunnerConnected += r => connected = r;
        server.RunnerDisconnected += r => disconnected = r;

        var machine = Guid.NewGuid();
        var info = server.ConnectRunner(machine, "conn-x");

        Assert.Equal(machine, info.MachineId);
        Assert.Equal("conn-x", info.ConnectionId);
        Assert.Equal(machine, connected!.MachineId);
        Assert.True(server.IsRunnerConnected(machine));
        Assert.Contains(server.ListRunners(), r => r.MachineId == machine);

        server.DisconnectRunner(machine);

        Assert.Equal(machine, disconnected!.MachineId);
        Assert.False(server.IsRunnerConnected(machine));
        Assert.Empty(server.ListRunners());
    }

    [Fact]
    public void DisconnectRunnerByConnection_removes_the_runner_and_raises_the_event()
    {
        var server = NewServer(new FakeCommandLineRunnerFactory());
        var machine = Guid.NewGuid();
        server.ConnectRunner(machine, "conn-1");

        RunnerInfo? disconnected = null;
        server.RunnerDisconnected += r => disconnected = r;

        server.DisconnectRunnerByConnection("conn-1");   // what RunnerHub.OnDisconnected calls

        Assert.Equal(machine, disconnected!.MachineId);
        Assert.False(server.IsRunnerConnected(machine));

        // An unknown / stale connection is a no-op (no event, no throw).
        disconnected = null;
        server.DisconnectRunnerByConnection("stale-conn");
        Assert.Null(disconnected);
    }

    [Fact]
    public async Task StartSessionAsync_registers_the_session_and_raises_SessionStarted()
    {
        var runner = new FakeCommandLineRunnerFactory();
        var server = NewServer(runner);

        AgentSessionInfo? started = null;
        server.SessionStarted += info => started = info;

        var (_, session) = await StartClaudeAsync(runner, server);

        Assert.NotNull(started);
        Assert.Equal(session.SessionId, started!.SessionId);
        Assert.Equal(AgentToolKey.ClaudeCodeCli, started.Tool);
        Assert.Null(started.RunnerMachineId);   // local

        Assert.Equal(session.SessionId, Assert.Single(server.ListSessions()).SessionId);
        Assert.Same(session, server.GetSession(session.SessionId));

        await server.StopSessionAsync(session.SessionId);
    }

    [Fact]
    public async Task StopSessionAsync_deregisters_and_raises_SessionEnded()
    {
        var runner = new FakeCommandLineRunnerFactory();
        var server = NewServer(runner);
        var (_, session) = await StartClaudeAsync(runner, server);

        AgentSessionInfo? ended = null;
        server.SessionEnded += info => ended = info;

        Assert.True(await server.StopSessionAsync(session.SessionId));
        Assert.NotNull(ended);
        Assert.Equal(session.SessionId, ended!.SessionId);

        Assert.Empty(server.ListSessions());
        Assert.Null(server.GetSession(session.SessionId));
        Assert.False(await server.StopSessionAsync(session.SessionId));   // already gone
    }
}
