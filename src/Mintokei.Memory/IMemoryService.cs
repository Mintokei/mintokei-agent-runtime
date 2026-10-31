using System.Text.Json;

namespace Mintokei.Memory;

/// <summary>Store original completed turns and retrieve authorized memory in the host process.</summary>
public interface IMemoryService
{
    Task<MemoryIngestionReceipt> StoreTurnAsync(CompletedMemoryTurn turn, CancellationToken ct = default);
    Task<JsonElement> SearchAsync(MemorySearch search, CancellationToken ct = default);
    Task<JsonElement> LoadEvidenceAsync(IReadOnlyList<Guid> sourceIds, CancellationToken ct = default);
}
