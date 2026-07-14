
namespace Mintokei.Runner.Host.Domain.Machines;

public class RunnerMachine
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? SecretHash { get; set; }
    public DateTimeOffset? SecretIssuedAt { get; set; }
    public DateTimeOffset? EnrolledAt { get; set; }
    public bool IsLocal { get; set; }
    public RunnerMachineStatus Status { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }
    public string? RunnerVersion { get; set; }
    public string? OsInfo { get; set; }
    public string? WorkspaceRootPath { get; set; }
    public int MaxInstances { get; set; } = 5;

    // Sequence tracking
    public long NextOutboundSequence { get; set; } = 1;
    public long LastAckedOutboundSequence { get; set; }
    public long LastReceivedInboundSequence { get; set; }

    public ICollection<OutboxMessage> OutboxMessages { get; set; } = [];
    public ICollection<RunnerOutboxChannel> OutboxChannels { get; set; } = [];
    // Cross-boundary navigations (Workspaces / AgentCapacities / Clis) are intentionally omitted:
    // RunnerMachine is destined for Mintokei.Runner.Host and must not reference product entities.
    // The FK-owning side stays on Workspace / RunnerMachineAgentCapacity / RunnerMachineCli — query
    // those tables by RunnerMachineId instead of navigating from the machine.

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
