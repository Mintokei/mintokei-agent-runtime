using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.AgentEngine.Contracts;
using Xunit;

namespace Mintokei.AgentControlPlane.Tests;

public sealed class ControlPlaneRegistrationTests
{
    [Fact]
    public void AddAgentControlPlane_RegistersOneSharedSingletonForAllPublicSeams()
    {
        using var provider = CreateProvider();

        var controlPlane = provider.GetRequiredService<IAgentControlPlane>();
        var runnerRegistry = provider.GetRequiredService<IRunnerRegistry>();
        var capacityLedger = provider.GetRequiredService<ICapacityLedger>();

        Assert.Same(controlPlane, runnerRegistry);
        Assert.Same(controlPlane, capacityLedger);
    }

    [Fact]
    public void DisconnectRunnerByConnection_IgnoresStaleConnectionIdAfterReconnect()
    {
        using var provider = CreateProvider();
        var controlPlane = provider.GetRequiredService<IAgentControlPlane>();
        var machineId = Guid.NewGuid();

        controlPlane.ConnectRunner(machineId, "conn-1");
        controlPlane.ConnectRunner(machineId, "conn-2");
        controlPlane.DisconnectRunnerByConnection("conn-1");

        Assert.True(controlPlane.IsRunnerConnected(machineId));
        Assert.Equal("conn-2", controlPlane.GetConnectionId(machineId));
        var runner = Assert.Single(controlPlane.ListRunners());
        Assert.Equal(machineId, runner.MachineId);
        Assert.Equal("conn-2", runner.ConnectionId);
    }

    [Fact]
    public async Task RegisterSession_TracksCapacityAndStopSessionDisposes()
    {
        using var provider = CreateProvider();
        var controlPlane = provider.GetRequiredService<IAgentControlPlane>();
        var capacityLedger = provider.GetRequiredService<ICapacityLedger>();

        var sessionKey = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var machineId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var session = new FakeSession(sessionId);

        AgentSessionInfo? started = null;
        AgentSessionInfo? ended = null;
        controlPlane.SessionStarted += info => started = info;
        controlPlane.SessionEnded += info => ended = info;

        controlPlane.RegisterSession(sessionKey, session, AgentToolKey.ClaudeCodeCli, machineId, agentId);

        Assert.NotNull(started);
        Assert.Equal(sessionKey, started!.Key);
        Assert.Equal(sessionId, started.SessionId);
        Assert.Equal(1, capacityLedger.CountByMachine(machineId));
        Assert.Equal(1, capacityLedger.CountByMachineAndAgent(machineId, agentId));

        controlPlane.SetIdleSince(sessionKey, DateTimeOffset.UtcNow);
        Assert.NotNull(Assert.Single(capacityLedger.GetSlots()).IdleSince);

        controlPlane.ClearIdleSince(sessionKey);
        Assert.Null(Assert.Single(capacityLedger.GetSlots()).IdleSince);

        var stopped = await controlPlane.StopSessionAsync(sessionKey);

        Assert.True(stopped);
        Assert.True(session.WasDisposed);
        Assert.NotNull(ended);
        Assert.Equal(sessionKey, ended!.Key);
        Assert.Empty(controlPlane.ListSessions());
        Assert.Empty(capacityLedger.GetSlots());
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<ICommandLineRunnerFactory, StubRunnerFactory>();
        services.AddAgentControlPlane();
        return services.BuildServiceProvider();
    }

    private sealed class StubRunnerFactory : ICommandLineRunnerFactory
    {
        public ICommandLineRunner Create(Guid? runnerMachineId)
            => throw new NotSupportedException("The tests exercise registration and capacity behavior only.");
    }

    private sealed class FakeSession(Guid sessionId) : IAgentSession
    {
        public Guid SessionId { get; } = sessionId;
        public string? AgentSessionId => null;
        public bool HasExited { get; private set; }
        public bool WasDisposed { get; private set; }
        public IAsyncEnumerable<AgentStreamOutput> Output => EmptyOutput();

        public Task StartAsync(bool resume, CancellationToken ct) => Task.CompletedTask;
        public Task AttachAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMessageAsync(string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendTurnAsync(SessionTurn turn, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RespondAsync(string requestId, UserInteractionResponse decision, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> InterruptAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task CompactAsync(string? instructions, CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackAsync(int numTurns, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ApplyConfigAsync(
            Dictionary<string, string?> oldConfig,
            Dictionary<string, string?> newConfig,
            CancellationToken ct = default)
            => Task.FromResult(false);

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            HasExited = true;
            return ValueTask.CompletedTask;
        }

        private static async IAsyncEnumerable<AgentStreamOutput> EmptyOutput()
        {
            yield break;
        }
    }
}
