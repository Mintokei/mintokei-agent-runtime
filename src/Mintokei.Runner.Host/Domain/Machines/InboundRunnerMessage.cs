
namespace Mintokei.Runner.Host.Domain.Machines;

public class InboundRunnerMessage
{
    public Guid Id { get; set; }
    public Guid RunnerMachineId { get; set; }
    public RunnerMachine RunnerMachine { get; set; } = null!;
    public long SequenceNumber { get; set; }
    public InboundMessageType MessageType { get; set; }
    public string? PayloadJson { get; set; }
    public Guid? CorrelationId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
