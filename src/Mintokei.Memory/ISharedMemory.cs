using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Mintokei.Memory;

public interface ISharedMemory
{
    Task<JsonElement> IngestAsync(CompletedMemoryTurn turn, CancellationToken ct = default);
    Task<JsonElement> SearchAsync(MemorySearch search, CancellationToken ct = default);
    Task<JsonElement> LoadAsync(IReadOnlyList<Guid> sourceIds, CancellationToken ct = default);
}
