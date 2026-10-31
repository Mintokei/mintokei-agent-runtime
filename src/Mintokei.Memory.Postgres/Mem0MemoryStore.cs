using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mintokei.Memory.Postgres;

public class Mem0MemoryStore(HttpClient http, IConfiguration config, IDbContextFactory<MemoryDbContext> factory) : IMemoryStore
{
    private string Url => config["Memory:Mem0Url"] ?? "http://127.0.0.1:58000";

    public async Task<MemoryWriteResult> AddAsync(MemoryRecord m, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(Url + "/memories", new { id = m.Id, text = m.CanonicalText, userId = m.UserId, ns = m.Namespace, validFrom = m.ValidFrom }, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonNode>(ct);
        return new(m.Id, body?["id"]?.ToString());
    }

    public async Task<MemorySearchResult> SearchAsync(MemorySearchRequest r, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(Url + "/search", new { query = r.Query, userId = r.Scope.UserId, ns = r.Scope.Namespace, limit = r.Limit }, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonArray>(ct);
        var ids = body!.Select(x => Guid.TryParse(x?["domainId"]?.ToString(), out var id) ? id : Guid.Empty).ToArray();
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Memories.Where(x => ids.Contains(x.Id) && x.UserId == r.Scope.UserId && x.Namespace == r.Scope.Namespace && x.ValidFrom <= r.Scope.AsOf).ToListAsync(ct);
        return new(rows.Select(x => new SearchCandidate(x.Id, "memory", null, null, x.ValidFrom, x.CanonicalText, body!.First(y => y?["domainId"]?.ToString() == x.Id.ToString())?["score"]?.GetValue<double>() ?? 0, "mem0")).ToList());
    }

    public async Task<MemoryRecord?> GetAsync(Guid memoryId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Memories.SingleOrDefaultAsync(x => x.Id == memoryId && x.UserId == "local", ct);
    }

    public async Task UpdateAsync(MemoryRecord memory, CancellationToken ct) => await AddAsync(memory, ct);
    public async Task DeleteAsync(MemoryRecord memory, CancellationToken ct)
    {
        using var response = await http.DeleteAsync(Url + "/memories/" + memory.Id + "?owner=" + Uri.EscapeDataString(memory.UserId + ":" + memory.Namespace), ct);
        response.EnsureSuccessStatusCode();
    }
}
