namespace Mintokei.AgentEngine;

/// <summary>Protocol semantics, not product/tool names. Unknown protocols reject busy input.</summary>
public enum InputDeliveryMode
{
    RejectWhileBusy,
    QueueWhileBusy,
    ExplicitSteering,
    CorrelatedStreaming,
}
