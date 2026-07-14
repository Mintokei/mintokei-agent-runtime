namespace Mintokei.Runner.Host.Domain.Machines;

/// <summary>
/// Per-process-correlation delivery state on the API↔Runner outbox.
///
/// The existing <see cref="RunnerMachine"/> tracks one global cumulative ack
/// per machine, which is what SignalR uses today. The gRPC migration opens
/// one logical stream per process correlation (see proto <c>OpenTask</c>),
/// each with its own monotonically increasing sequence and its own cumulative
/// ack — so that one task's stuck/poison message cannot block delivery to
/// any other task on the same machine.
///
/// One row per (machine, correlation). Created lazily when the first message
/// for a correlation is enqueued (or when the runner first opens an OpenTask
/// stream for it). <see cref="ClosedAt"/> is set when the runner reports the
/// process completed and outbox cleanup may proceed for that correlation.
///
/// Coexists with the existing per-machine sequence on <c>RunnerMachine</c>:
/// SignalR continues to use the per-machine fields; gRPC OpenTask uses these
/// per-correlation fields. After the cutover and SignalR removal, the
/// per-machine sequence fields can be retired.
/// </summary>
public class RunnerOutboxChannel
{
    public Guid Id { get; set; }

    public Guid RunnerMachineId { get; set; }
    public RunnerMachine RunnerMachine { get; set; } = null!;

    /// <summary>Process correlation identifier — matches <see cref="OutboxMessage.CorrelationId"/>.</summary>
    public Guid CorrelationId { get; set; }

    /// <summary>Highest server→runner sequence the runner has acknowledged for this correlation.</summary>
    public long LastAckedOutboundSequence { get; set; }

    /// <summary>Highest runner→server sequence the API has received for this correlation.</summary>
    public long LastReceivedInboundSequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set when the correlation's process has exited and its stream is closed.</summary>
    public DateTimeOffset? ClosedAt { get; set; }
}
