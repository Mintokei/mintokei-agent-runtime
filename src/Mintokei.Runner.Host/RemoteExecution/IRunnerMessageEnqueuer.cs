
namespace Mintokei.Runner.Host.RemoteExecution;

/// <summary>
/// Enqueues durable outbox messages destined for a remote runner machine.
/// Messages are persisted to SQLite and dispatched by the OutboxProcessorService.
/// </summary>
public interface IRunnerMessageEnqueuer
{
    Task<long> EnqueueAsync(
        Guid machineId,
        OutboxMessageType type,
        object payload,
        Guid? correlationId = null,
        TimeSpan? ttl = null,
        DateTimeOffset? deliverAfterUtc = null,
        CancellationToken ct = default);
}
