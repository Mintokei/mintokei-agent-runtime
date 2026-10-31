namespace Mintokei.Memory;

public sealed record MemoryReasoningRequest(
    string Operation,
    string SystemPrompt,
    string Prompt,
    string JsonSchema,
    int TimeoutSeconds,
    Guid CorrelationId);
