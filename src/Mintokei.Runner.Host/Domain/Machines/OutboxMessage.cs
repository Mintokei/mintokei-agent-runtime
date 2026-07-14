
namespace Mintokei.Runner.Host.Domain.Machines;

public class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid RunnerMachineId { get; set; }
    public RunnerMachine RunnerMachine { get; set; } = null!;
    public long SequenceNumber { get; set; }
    public OutboxMessageType MessageType { get; set; }
    public OutboxMessageStatus Status { get; set; }
    public required string PayloadJson { get; set; }
    public Guid? CorrelationId { get; set; }
    public int DeliveryAttempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? AckedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? DeliverAfterUtc { get; set; }
}
