namespace Mintokei.Runner.Host.Domain.Machines.Enums;

public enum OutboxMessageStatus
{
    Pending,
    Sent,
    Acknowledged,
    Expired,
    Failed
}
