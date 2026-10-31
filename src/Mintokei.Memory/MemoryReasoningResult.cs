namespace Mintokei.Memory;

/// <summary>Only observed output and host-selected configuration; unavailable usage stays absent.</summary>
public sealed record MemoryReasoningResult(
    bool Success,
    string? Output,
    string? Error,
    string? AgentTool = null,
    string? RequestedModel = null,
    string? RequestedEffort = null,
    Guid? AgentId = null,
    Guid? RunnerMachineId = null);
