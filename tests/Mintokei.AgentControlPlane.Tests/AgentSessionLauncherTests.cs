using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.CommandRunner;
using Xunit;

namespace Mintokei.AgentControlPlane.Tests;

/// <summary>
/// Tests for the DB-free <see cref="AgentSessionLauncher"/> — driven by a fake
/// <see cref="ICommandLineRunnerFactory"/> so no real CLI is spawned. Cover: listing connected runners
/// from the tracker, rejecting a disconnected remote runner, and spawning + handshaking a Claude
/// session end-to-end (backend builds the command → runner starts it → session handshakes).
/// </summary>
public class AgentSessionLauncherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static AgentSessionLauncher NewLauncher(FakeCommandLineRunnerFactory runner, RunnerConnectionTracker tracker)
        => new([new ClaudeBackend()], runner, tracker, NullLoggerFactory.Instance);

    private static string RequestIdOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("request_id").GetString()!;
    }

    private static string ControlResponse(string requestId, string subtype)
        => JsonSerializer.Serialize(new { type = "control_response", response = new { request_id = requestId, subtype } });

    [Fact]
    public void ListConnectedRunners_reflects_the_tracker()
    {
        var tracker = new RunnerConnectionTracker();
        var launcher = NewLauncher(new FakeCommandLineRunnerFactory(), tracker);

        Assert.Empty(launcher.ListConnectedRunners());

        var machine = Guid.NewGuid();
        tracker.Register(machine, "conn-1");

        Assert.Contains(machine, launcher.ListConnectedRunners());
        Assert.True(launcher.IsRunnerConnected(machine));
    }

    [Fact]
    public async Task StartSessionAsync_rejects_a_disconnected_remote_runner()
    {
        var launcher = NewLauncher(new FakeCommandLineRunnerFactory(), new RunnerConnectionTracker());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            launcher.StartSessionAsync(
                new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli }, runnerMachineId: Guid.NewGuid()));
    }

    [Fact]
    public async Task StartSessionAsync_local_builds_the_command_spawns_and_handshakes_a_claude_session()
    {
        var runner = new FakeCommandLineRunnerFactory();
        var launcher = NewLauncher(runner, new RunnerConnectionTracker());

        var startTask = launcher.StartSessionAsync(
            new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli, WorkingDirectory = "/work" });

        // Complete the Claude initialize handshake.
        var initLine = await runner.Handle.WaitForWriteAsync(l => l.Contains("\"subtype\":\"initialize\""), Timeout);
        runner.Handle.FeedStdout(ControlResponse(RequestIdOf(initLine), "success"));

        var session = await startTask.WaitAsync(Timeout);

        Assert.NotNull(session);
        Assert.Equal("claude", runner.LastOptions!.Executable);   // backend built Claude's command line
        Assert.Null(runner.LastMachineId);                        // local runner
        Assert.True(runner.LastOptions.Arguments!.ContainsKey("--input-format"));

        await session.DisposeAsync();
    }
}
