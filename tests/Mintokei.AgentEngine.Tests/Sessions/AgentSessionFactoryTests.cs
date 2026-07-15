using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Pins the one thing that lets the engine run an agent CLI on a <em>remote</em> machine:
/// <see cref="AgentSessionFactory"/> forwards <c>runnerMachineId</c> to the injected
/// <see cref="Mintokei.AgentEngine.CommandRunner.ICommandLineRunnerFactory"/>. Here that factory is a
/// fake that records the id; in a host that registers <c>Mintokei.Runner.Host</c>'s factory the same
/// id dispatches the CLI over the durable outbox + gRPC to that runner. The engine stays
/// transport-agnostic — it only forwards the opaque id.
/// </summary>
public class AgentSessionFactoryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CreateClaudeSessionAsync_spawns_the_cli_on_the_requested_remote_machine()
    {
        var runnerFactory = new FakeCommandLineRunnerFactory();
        var sessionFactory = new AgentSessionFactory(runnerFactory, NullLoggerFactory.Instance);
        var machineId = Guid.NewGuid();
        var spec = new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli, WorkingDirectory = "/repo" };

        await using var session = await HandshakeAsync(
            sessionFactory.CreateClaudeSessionAsync(spec, runnerMachineId: machineId, ct: TestContext.Current.CancellationToken), runnerFactory);

        Assert.Equal(machineId, runnerFactory.LastMachineId);   // routed to the REMOTE machine
        Assert.NotNull(runnerFactory.LastOptions);              // a real CLI invocation was built + started
    }

    [Fact]
    public async Task CreateClaudeSessionAsync_defaults_to_local_when_no_machine_id_is_given()
    {
        var runnerFactory = new FakeCommandLineRunnerFactory();
        var sessionFactory = new AgentSessionFactory(runnerFactory, NullLoggerFactory.Instance);
        var spec = new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli };

        await using var session = await HandshakeAsync(
            sessionFactory.CreateClaudeSessionAsync(spec, ct: TestContext.Current.CancellationToken), runnerFactory);

        Assert.Null(runnerFactory.LastMachineId);               // null id ⇒ local
    }

    [Fact]
    public async Task CreateSessionAsync_threads_the_machine_id_for_any_backend()
    {
        var runnerFactory = new FakeCommandLineRunnerFactory();
        var sessionFactory = new AgentSessionFactory(runnerFactory, NullLoggerFactory.Instance);
        var machineId = Guid.NewGuid();
        var spec = new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli };

        await using var session = await HandshakeAsync(
            sessionFactory.CreateSessionAsync(new ClaudeBackend(), spec, runnerMachineId: machineId, ct: TestContext.Current.CancellationToken),
            runnerFactory);

        Assert.Equal(machineId, runnerFactory.LastMachineId);
    }

    // The create call awaits the Claude handshake (initialize control_request → control_response);
    // feed the matching response on the fake process so the create task returns a ready session.
    private static async Task<IAgentSession> HandshakeAsync(
        Task<IAgentSession> createTask, FakeCommandLineRunnerFactory runnerFactory)
    {
        var initLine = await runnerFactory.Handle.WaitForWriteAsync(
            l => l.Contains("\"subtype\":\"initialize\""), Timeout);
        runnerFactory.Handle.FeedStdout(ControlResponse(RequestIdOf(initLine), "success"));
        return await createTask.WaitAsync(Timeout);
    }

    private static string RequestIdOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("request_id").GetString()!;
    }

    private static string ControlResponse(string requestId, string subtype)
        => JsonSerializer.Serialize(new { type = "control_response", response = new { request_id = requestId, subtype } });
}
