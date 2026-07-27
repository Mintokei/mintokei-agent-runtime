using System.Runtime.CompilerServices;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;
using Mintokei.Runner.Host.Server;
using Mintokei.Sandbox;

namespace Mintokei.Sandbox.Hosting.Tests;

/// <summary>Records what was provisioned/stopped and can pretend the container exited (so the online-wait
/// bails immediately instead of burning a timeout). Also serves canned logs, like the real backends do.</summary>
internal sealed class FakeRuntime : ISandboxRuntime, ISandboxLogSource
{
    public List<SandboxSpec> Provisioned { get; } = [];
    public List<string> Stopped { get; } = [];
    public SandboxState Status { get; set; } = SandboxState.Running;
    public int? ExitCode { get; set; }
    public string Logs { get; set; } = "boom: could not clone repo";

    public string Backend => "fake";

    public Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        Provisioned.Add(spec);
        return Task.FromResult(new SandboxHandle($"id-{spec.Name}", spec.Name, Backend));
    }

    public Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct = default)
        => Task.FromResult(new SandboxStatus(Status, ExitCode));

    public Task StopAsync(SandboxHandle handle, CancellationToken ct = default)
    {
        Stopped.Add(handle.Name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SandboxHandle>> ListManagedAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SandboxHandle>>([]);

    public Task<string> GetLogsAsync(SandboxHandle handle, int tailLines = 40, CancellationToken ct = default)
        => Task.FromResult(Logs);
}

/// <summary>No broker secrets — the facade's tests exercise the spec/allowlist wiring, not credential minting
/// (the real providers have their own tests). Mirrors the library's internal no-op default.</summary>
internal sealed class NoBrokerSecrets : ISandboxBrokerSecretsProvider
{
    public Task<SandboxBrokerSecrets?> ResolveAsync(
        SandboxSessionRequest request, SandboxProfile profile, CancellationToken ct = default)
        => Task.FromResult<SandboxBrokerSecrets?>(null);
}

/// <summary>Mints a predictable token + pre-created machine id, and records what it was asked for.</summary>
internal sealed class FakeEnrollment : IRunnerEnrollment
{
    public Guid MachineId { get; set; } = Guid.NewGuid();
    public string Token { get; set; } = "enrollment-token";
    public string? RequestedMachineName { get; private set; }
    public bool RequestedEphemeral { get; private set; }
    public string? RequestedProfile { get; private set; }

    public Task<CreateEnrollmentTokenResult> CreateEnrollmentTokenAsync(
        string? createdByUserId = null, string? createdByUserName = null,
        string? machineName = null, bool isEphemeral = false, string? profile = null)
    {
        RequestedMachineName = machineName;
        RequestedEphemeral = isEphemeral;
        RequestedProfile = profile;
        return Task.FromResult(new CreateEnrollmentTokenResult(
            Token, "prefix", DateTimeOffset.UtcNow.AddMinutes(15), MachineId));
    }

    public Task<RunnerHostResult<EnrollMachineResult>> EnrollAsync(EnrollMachineCommand command) =>
        throw new NotSupportedException();
}

/// <summary>In-memory control plane: reports a machine as connected (or not), hands out a fake session, and
/// records the dispatch so tests can assert which machine + working directory the session landed on.</summary>
internal sealed class FakeControlPlane : IAgentControlPlane
{
    public bool Connected { get; set; } = true;
    public Exception? StartThrows { get; set; }
    public FakeSession Session { get; } = new();
    public Guid? StartedOnMachine { get; private set; }
    public AgentSessionSpec? StartedSpec { get; private set; }
    public List<Guid> Stopped { get; } = [];

    public Task<IAgentSession> StartSessionAsync(
        AgentSessionSpec spec, Guid? runnerMachineId = null, Guid? agentId = null,
        AgentSessionOptions? options = null, CancellationToken ct = default)
        => StartSessionAsync(null, spec, runnerMachineId, agentId, options, ct);

    public Task<IAgentSession> StartSessionAsync(
        Guid? sessionKey, AgentSessionSpec spec, Guid? runnerMachineId = null, Guid? agentId = null,
        AgentSessionOptions? options = null, CancellationToken ct = default)
    {
        if (StartThrows is not null)
            throw StartThrows;
        StartedOnMachine = runnerMachineId;
        StartedSpec = spec;
        return Task.FromResult<IAgentSession>(Session);
    }

    public Task<bool> StopSessionAsync(Guid key)
    {
        Stopped.Add(key);
        return Task.FromResult(true);
    }

    public bool IsRunnerConnected(Guid machineId) => Connected;

    // Unused by the facade.
    public IReadOnlyList<AgentSessionInfo> ListSessions() => [];
    public event Action<AgentSessionInfo>? SessionStarted { add { } remove { } }
    public event Action<AgentSessionInfo>? SessionEnded { add { } remove { } }
    public void RegisterSession(Guid sessionKey, IAgentSession session, AgentToolKey tool, Guid? machineId = null, Guid? agentId = null) { }
    public bool DeregisterSession(Guid sessionKey, IAgentSession session) => true;
    public IAgentSession? GetSession(Guid key) => null;
    public void SetIdleSince(Guid key, DateTimeOffset idleSince) { }
    public void ClearIdleSince(Guid key) { }
    public IReadOnlyList<RunnerInfo> ListRunners() => [];
    public Guid? GetMachineId(string connectionId) => null;
    public string? GetConnectionId(Guid machineId) => null;
    public RunnerInfo ConnectRunner(Guid machineId, string connectionId) => new(machineId, connectionId);
    public void DisconnectRunner(Guid machineId) { }
    public void DisconnectRunnerByConnection(string connectionId) { }
    public event Action<RunnerInfo>? RunnerConnected { add { } remove { } }
    public event Action<RunnerInfo>? RunnerDisconnected { add { } remove { } }
}

/// <summary>A session that records sent messages and emits a scripted output stream.</summary>
internal sealed class FakeSession : IAgentSession
{
    public List<string> Sent { get; } = [];
    public List<AgentStreamOutput> Script { get; } = [];
    public bool Disposed { get; private set; }

    public Guid SessionId { get; } = Guid.NewGuid();
    public string? AgentSessionId => null;
    public bool HasExited => false;

    public IAsyncEnumerable<AgentStreamOutput> Output => Emit();

    private async IAsyncEnumerable<AgentStreamOutput> Emit([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in Script)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
        await Task.CompletedTask;
    }

    public Task SendMessageAsync(string content, CancellationToken ct = default)
    {
        Sent.Add(content);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    // Unused by the facade.
    public Task StartAsync(bool resume, CancellationToken ct) => Task.CompletedTask;
    public Task AttachAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SendTurnAsync(SessionTurn turn, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> RespondAsync(string requestId, UserInteractionResponse decision, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> InterruptAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task CompactAsync(string? instructions, CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackAsync(int numTurns, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> ApplyConfigAsync(
        Dictionary<string, string?> oldConfig, Dictionary<string, string?> newConfig, CancellationToken ct = default)
        => Task.FromResult(false);
}
