namespace Mintokei.Memory;

public sealed record MemoryEvidenceRequest(
    IReadOnlyList<Guid> SourceIds,
    MemorySearchScope Scope = MemorySearchScope.AllAuthorizedHistory,
    Guid? CurrentTaskId = null);
