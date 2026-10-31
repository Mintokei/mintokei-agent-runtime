namespace Mintokei.Memory;

/// <summary>Acceptance/checkpoint status, not a promise that semantic indexing has completed.</summary>
public sealed record MemoryIngestionReceipt(Guid ConversationId, Guid TurnId, string Status, bool Duplicate);
