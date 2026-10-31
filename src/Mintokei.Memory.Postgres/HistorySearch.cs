using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mintokei.Memory.Postgres;

public class HistorySearch(NpgsqlDataSource source, IDbContextFactory<MemoryDbContext> factory) : IHistorySearch
{
    public async Task<IReadOnlyList<SearchCandidate>> SearchAsync(SearchScope scope, string query, int limit, string temporalHint, CancellationToken ct)
    {
        var order = temporalHint is "first" or "original" or "beginning" ? "m.\"CreatedAt\" ASC, m.\"SequenceNumber\" ASC" : temporalHint is "latest" or "previous" ? "m.\"CreatedAt\" DESC, m.\"SequenceNumber\" DESC" : "score DESC";
        await using var cmd = source.CreateCommand("""
            SELECT m."Id",m."ConversationId",m."SequenceNumber",m."CreatedAt",m."Content",m."Role",
            ts_rank_cd(to_tsvector('english',m."Content"),websearch_to_tsquery('english',@query))+similarity(m."Content",@query) score,
            m."SequenceNumber">c."LastProcessedSequence" provisional
            FROM "Messages" m JOIN "Conversations" c ON c."Id"=m."ConversationId"
            WHERE c."UserId"=@owner AND c."Namespace"=@ns AND m."CreatedAt"<=@asof
            AND (NOT @local OR c."Id"=@conversation) AND m."Id"<>@excluded
            AND m."SequenceNumber"<=c."VisibleSequence" AND (c."Id"<>@conversation OR m."SequenceNumber"<=@seq)
            AND (to_tsvector('english',m."Content") @@ websearch_to_tsquery('english',@query) OR similarity(m."Content",@query)>0.06)
            ORDER BY
            """ + " " + order + " LIMIT @limit");
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("owner", scope.UserId);
        cmd.Parameters.AddWithValue("ns", scope.Namespace);
        cmd.Parameters.AddWithValue("asof", scope.AsOf);
        cmd.Parameters.AddWithValue("conversation", scope.ConversationId);
        cmd.Parameters.AddWithValue("seq", scope.ThroughSequence);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("local", scope.CurrentConversationOnly);
        cmd.Parameters.AddWithValue("excluded", scope.ExcludeMessageId ?? Guid.Empty);
        var list = new List<SearchCandidate>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                list.Add(new(reader.GetGuid(0), "message", reader.GetGuid(1), reader.GetInt64(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4), reader.GetFloat(6), "full_text+trigram", Json.Write(new { role = reader.GetString(5), provisional = reader.GetBoolean(7) })));
        return list;
    }

    public async Task<IReadOnlyList<Message>> GetRecentMessagesAsync(Guid conversationId, long upToSequence, int maxMessages, int maxTokens, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var c = await db.Conversations.SingleAsync(x => x.Id == conversationId && x.UserId == "local", ct);
        var list = await db.Messages.Where(x => x.ConversationId == conversationId && x.SequenceNumber <= upToSequence && x.SequenceNumber <= c.VisibleSequence).OrderByDescending(x => x.SequenceNumber).Take(maxMessages).ToListAsync(ct);
        var selected = new List<Message>();
        var budget = maxTokens * 4;
        foreach (var m in list)
        {
            if (m.Content.Length > budget)
                break;
            selected.Add(m);
            budget -= m.Content.Length;
        }

        selected.Reverse();
        return selected;
    }

    public async Task<List<Message>> ExpandAsync(SearchScope scope, IEnumerable<SearchCandidate> candidates, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var result = new List<Message>();
        foreach (var c in candidates.Where(x => x.SourceType == "message").Take(8))
        {
            var rows = await (
                from m in db.Messages
                join conv in db.Conversations on m.ConversationId equals conv.Id
                where conv.UserId == scope.UserId && conv.Namespace == scope.Namespace && (!scope.CurrentConversationOnly || conv.Id == scope.ConversationId) && m.Id != (scope.ExcludeMessageId ?? Guid.Empty) && m.ConversationId == c.ConversationId && m.SequenceNumber >= c.SequenceNumber - 1 && m.SequenceNumber <= c.SequenceNumber + 1 && m.SequenceNumber <= conv.VisibleSequence && m.CreatedAt <= scope.AsOf && (m.ConversationId != scope.ConversationId || m.SequenceNumber <= scope.ThroughSequence)select m).ToListAsync(ct);
            result.AddRange(rows);
        }

        return result.DistinctBy(x => x.Id).OrderBy(x => x.CreatedAt).ThenBy(x => x.SequenceNumber).ToList();
    }

    public async Task<List<SearchCandidate>> MemoryLexicalAsync(SearchScope scope, string query, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var allowedIds = scope.CurrentConversationOnly
            ? await MemoryQueries.ForScope(db, scope).Select(m => m.Id).ToArrayAsync(ct) : [];
        if (scope.CurrentConversationOnly && allowedIds.Length == 0)
            return [];
        await using var cmd = source.CreateCommand("""
            SELECT "Id","CanonicalText","ValidFrom",ts_rank_cd(to_tsvector('english',"CanonicalText"),websearch_to_tsquery('english',@q))+similarity("CanonicalText",@q) score
            FROM "Memories" WHERE "UserId"=@owner AND "Namespace"=@ns AND "ValidFrom"<=@asof AND "RecordedAt"<=@asof
            AND (NOT @local OR "Id"=ANY(@allowed))
            AND (to_tsvector('english',"CanonicalText") @@ websearch_to_tsquery('english',@q) OR similarity("CanonicalText",@q)>.06) ORDER BY score DESC LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("local", scope.CurrentConversationOnly);
        cmd.Parameters.AddWithValue("allowed", allowedIds);
        cmd.Parameters.AddWithValue("q", query);
        cmd.Parameters.AddWithValue("owner", scope.UserId);
        cmd.Parameters.AddWithValue("ns", scope.Namespace);
        cmd.Parameters.AddWithValue("asof", scope.AsOf);
        cmd.Parameters.AddWithValue("limit", limit);
        var result = new List<SearchCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetGuid(0), "memory", null, null, reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(1), reader.GetFloat(3), "memory_lexical"));
        return result;
    }

    public static List<SearchCandidate> Fuse(IEnumerable<IEnumerable<SearchCandidate>> lists, int limit)
    {
        var scores = new Dictionary<Guid, (SearchCandidate Candidate, double Score, List<object> Signals)>();
        foreach (var list in lists)
        {
            int rank = 0;
            foreach (var x in list)
            {
                rank++;
                scores.TryGetValue(x.SourceId, out var old);
                var signals = old.Signals ?? [];
                signals.Add(new { method = x.RetrievalMethod, score = x.RetrievalScore, rank });
                scores[x.SourceId] = (old.Candidate ?? x, old.Score + 1.0 / (60 + rank), signals);
            }
        }

        return scores.Values.OrderByDescending(x => x.Score).Take(limit).Select(x => x.Candidate with { RetrievalScore = x.Score, RetrievalMethod = "rrf(k=60)", MetadataJson = Json.Write(new { signals = x.Signals, original = Json.Node(x.Candidate.MetadataJson) }) }).ToList();
    }
}
