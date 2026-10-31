using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Mintokei.Memory;
/// <summary>The host binds credentials and ownership. Neither models nor browsers choose the partition.</summary>
public sealed class HttpSharedMemory(HttpClient http) : ISharedMemory
{
    public Task<JsonElement> IngestAsync(CompletedMemoryTurn turn, CancellationToken ct = default) => Post("turns", turn, ct);
    public Task<JsonElement> SearchAsync(MemorySearch search, CancellationToken ct = default) => Post("search", search, ct);
    public Task<JsonElement> LoadAsync(IReadOnlyList<Guid> sourceIds, CancellationToken ct = default) => Post("sources", new { sourceIds }, ct);
    private async Task<JsonElement> Post(string path, object data, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/memory/v1/" + path, data, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }
}
