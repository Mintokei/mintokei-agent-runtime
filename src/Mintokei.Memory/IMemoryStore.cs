using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public interface IMemoryStore
{
    Task<MemoryWriteResult> AddAsync(MemoryRecord memory, CancellationToken cancellationToken);
    Task<MemorySearchResult> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken);
    Task<MemoryRecord?> GetAsync(Guid memoryId, CancellationToken cancellationToken);
    Task UpdateAsync(MemoryRecord memory, CancellationToken cancellationToken);
}
