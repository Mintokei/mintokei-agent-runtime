namespace Mintokei.Runner.Host.Domain.Machines.Enums;

public enum OutboxMessageType
{
    StartProcess,
    WriteStdin,
    KillProcess,
    HeartbeatPing
}
