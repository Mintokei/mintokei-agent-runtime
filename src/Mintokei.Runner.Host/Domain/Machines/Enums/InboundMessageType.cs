namespace Mintokei.Runner.Host.Domain.Machines.Enums;

public enum InboundMessageType
{
    ProcessOutputChunk,
    ProcessCompleted,
    HeartbeatPong,
    RunnerStatus,
    Acknowledgement
}
