using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mintokei.Memory.Postgres;

public class PostgresMemoryStore(NpgsqlDataSource source, IDbContextFactory<MemoryDbContext> factory, IEmbeddingProvider embeddings) : IMemoryStore
{
    public static string Vector(float[] values) => "[" + string.Join(',', values.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
    public async Task<MemoryWriteResult> AddAsync(MemoryRecord memory, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var vector = await embeddings.EmbedAsync(memory.CanonicalText, ct);
        await using var cmd = source.CreateCommand("""
            INSERT INTO memory_embeddings(memory_id,model,dimensions,embedding,duration_ms) VALUES(@id,@model,@dimensions,@vector::vector,@duration)
            ON CONFLICT(memory_id) DO UPDATE SET model=EXCLUDED.model,dimensions=EXCLUDED.dimensions,embedding=EXCLUDED.embedding,duration_ms=EXCLUDED.duration_ms,created_at=now()
            """);
        cmd.Parameters.AddWithValue("id", memory.Id);
        cmd.Parameters.AddWithValue("model", embeddings.Model);
        cmd.Parameters.AddWithValue("dimensions", embeddings.Dimensions);
        cmd.Parameters.AddWithValue("vector", Vector(vector));
        cmd.Parameters.AddWithValue("duration", watch.Elapsed.TotalMilliseconds);
        await cmd.ExecuteNonQueryAsync(ct);
        return new(memory.Id, null);
    }

    public async Task<MemorySearchResult> SearchAsync(MemorySearchRequest r, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var allowedIds = r.Scope.CurrentConversationOnly
            ? await MemoryQueries.ForScope(db, r.Scope).Select(m => m.Id).ToArrayAsync(ct) : [];
        if (r.Scope.CurrentConversationOnly && allowedIds.Length == 0)
            return new([]);
        var vector = await embeddings.EmbedAsync(r.Query, ct);
        await using var cmd = source.CreateCommand("""
            SELECT m."Id",m."CanonicalText",m."ValidFrom",1-(e.embedding <=> @vector::vector) score,m."Status",m."Scope"
            FROM memory_embeddings e JOIN "Memories" m ON e.memory_id=m."Id"
            WHERE m."UserId"=@owner AND m."Namespace"=@ns AND m."ValidFrom"<=@asof AND m."RecordedAt"<=@asof
            AND (NOT @local OR m."Id"=ANY(@allowed))
            AND e.model=@model AND e.dimensions=@dimensions
            ORDER BY e.embedding <=> @vector::vector LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("local", r.Scope.CurrentConversationOnly);
        cmd.Parameters.AddWithValue("allowed", allowedIds);
        cmd.Parameters.AddWithValue("owner", r.Scope.UserId);
        cmd.Parameters.AddWithValue("ns", r.Scope.Namespace);
        cmd.Parameters.AddWithValue("asof", r.Scope.AsOf);
        cmd.Parameters.AddWithValue("model", embeddings.Model);
        cmd.Parameters.AddWithValue("dimensions", embeddings.Dimensions);
        cmd.Parameters.AddWithValue("vector", Vector(vector));
        cmd.Parameters.AddWithValue("limit", r.Limit);
        var list = new List<SearchCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (reader.GetDouble(3) > .30)
                list.Add(new(reader.GetGuid(0), "memory", null, null, reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(1), reader.GetDouble(3), "semantic", Json.Write(new { status = reader.GetString(4), scope = reader.GetString(5), model = embeddings.Model, dimensions = embeddings.Dimensions })));
        return new(list);
    }

    public async Task<MemoryRecord?> GetAsync(Guid memoryId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Memories.SingleOrDefaultAsync(x => x.Id == memoryId && x.UserId == "local", ct);
    }

    public async Task UpdateAsync(MemoryRecord memory, CancellationToken ct) => await AddAsync(memory, ct);
}
