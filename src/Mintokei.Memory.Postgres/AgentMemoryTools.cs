using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Mintokei.Memory.Postgres;
// Tools execute fixed, owned, time-bounded queries. The model never supplies SQL,
// owner/namespace, an as-of boundary, filesystem paths or provider credentials.
public sealed class AgentMemoryTools(IDbContextFactory<MemoryDbContext> factory, HistorySearch history, PostgresMemoryStore postgres, Mem0MemoryStore mem0, IArtifactStore artifacts)
{
    public static JsonArray Definitions => Json.Read<JsonArray>("""
    [
      {"type":"function","name":"search_knowledge","description":"Search saved memories, original conversation messages (including unindexed messages), goals, entities, episodes and artifact metadata. Returns original evidence where available. Use short focused terms; refine if needed. Artifact contents require load_evidence.","strict":true,
       "parameters":{"type":"object","properties":{"query":{"type":"string"},"scope":{"type":"string","enum":["all","current_conversation"]},"temporal":{"type":"string","enum":["relevance","first","latest"]}},"required":["query","scope","temporal"],"additionalProperties":false}},
      {"type":"function","name":"load_evidence","description":"Load original source messages with neighbors, or artifact version contents using source/version IDs returned by search. Never use a path. Choose the requested approved/original/latest version explicitly.","strict":true,
       "parameters":{"type":"object","properties":{"sourceIds":{"type":"array","items":{"type":"string"}}},"required":["sourceIds"],"additionalProperties":false}}
    ]
    """);

    public static IQueryable<Message> Messages(MemoryDbContext db, SearchScope s) =>
        from m in db.Messages
        join c in db.Conversations on m.ConversationId equals c.Id
        where c.UserId == s.UserId && c.Namespace == s.Namespace && m.CreatedAt <= s.AsOf && m.SequenceNumber <= c.VisibleSequence && (!s.CurrentConversationOnly || c.Id == s.ConversationId) && (c.Id != s.ConversationId || m.SequenceNumber <= s.ThroughSequence) && m.Id != (s.ExcludeMessageId ?? Guid.Empty)select m;
    public async Task<AgentToolResult> Execute(string name, JsonObject args, SearchScope scope, MemoryConfiguration config, CancellationToken ct)
    {
        if (name == "load_evidence")
        {
            var raw = args["sourceIds"]?.AsArray() ?? throw new ArgumentException("sourceIds are required");
            if (raw.Count is < 1 or > 8 || raw.Any(x => !Guid.TryParse(x?.ToString(), out _)))
                throw new ArgumentException("Supply 1–8 valid source IDs");
            var requestedIds = raw.Select(x => Guid.Parse(x!.ToString())).Distinct().ToArray();
            var loaded = await Load(scope, requestedIds, true, ct);
            return new(Json.Node(Json.Write(new { sources = loaded, missingIds = requestedIds.Except(loaded.Select(x => x.Id)), note = "Missing sources may be outside the authorized/time scope. Excerpts can be truncated." }))!.AsObject(), loaded);
        }

        if (name != "search_knowledge")
            throw new ArgumentException("Unsupported memory tool");
        var query = args["query"]?.ToString().Trim() ?? "";
        var temporal = args["temporal"]?.ToString();
        var requestedScope = args["scope"]?.ToString();
        if (query.Length is < 1 or > 500 || temporal is not ("relevance" or "first" or "latest") || requestedScope is not ("all" or "current_conversation"))
            throw new ArgumentException("Invalid search arguments");
        var s = scope with
        {
            CurrentConversationOnly = scope.CurrentConversationOnly || requestedScope == "current_conversation"
        };
        var limit = Math.Min(config.TopK, 12);
        async Task<(string Strategy, List<SearchCandidate> Items, string? Error)> Search(string strategy, Func<Task<List<SearchCandidate>>> run)
        {
            try
            {
                return (strategy, await run(), null);
            }
            catch (Exception e)when (e is not OperationCanceledException)
            {
                return (strategy, [], e.Message);
            }
        }

        var tasks = new List<Task<(string Strategy, List<SearchCandidate> Items, string? Error)>>
        {
            Search("messages", async () => (await history.SearchAsync(s, query, limit, temporal == "relevance" ? "none" : temporal, ct)).ToList())
        };
        tasks.Add(Search("memories_lexical", () => history.MemoryLexicalAsync(s, query, limit, ct)));
        tasks.Add(Search("memories_semantic", async () => (await (config.MemoryProvider == "mem0" ? (IMemoryStore)mem0 : postgres).SearchAsync(new(s, query, limit), ct)).Items.ToList()));

        var searches = await Task.WhenAll(tasks);
        await using var db = await factory.CreateDbContextAsync(ct);
        var ranked = HistorySearch.Fuse(searches.Select(x => x.Items), limit * 2);
        var memoryIds = ranked.Where(x => x.SourceType == "memory").Select(x => x.SourceId).ToArray();
        var memories = await MemoryQueries.ForScope(db, s).Where(x => memoryIds.Contains(x.Id)).ToListAsync(ct);
        // Do not leak revisions written after a replay boundary.
        foreach (var m in memories.ToList())
        {
            var revisions = await db.MemoryRevisions.Where(r => r.MemoryId == m.Id).OrderByDescending(r => r.Version).ToListAsync(ct);
            if (revisions.Any(r => r.CreatedAt > s.AsOf))
            {
                memories.Remove(m);
                var past = revisions.FirstOrDefault(r => r.CreatedAt <= s.AsOf);
                if (past != null)
                    memories.Add(Json.Read<MemoryRecord>(past.SnapshotJson));
            }
        }

        memoryIds = memories.Select(x => x.Id).ToArray();
        var links = await db.MemorySources.Where(x => memoryIds.Contains(x.MemoryId) && x.CreatedAt <= s.AsOf).ToListAsync(ct);
        var ids = ranked.Where(x => x.SourceType == "message").Select(x => x.SourceId).Concat(links.Select(x => x.SourceId)).Distinct().Take(24).ToArray();
        var sources = await Load(s, ids, true, ct);
        var allowed = sources.Select(x => x.Id).ToHashSet();
        memories = memories.Where(m => links.Any(l => l.MemoryId == m.Id && allowed.Contains(l.SourceId))).ToList();
        var terms = Regex.Matches(query, @"[\p{L}\p{N}_.-]{3,}").Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        var eligibleMemories = MemoryQueries.ForScope(db, s);
        var knowledge = await db.Knowledge.Where(x => x.UserId == s.UserId && x.Namespace == s.Namespace && x.CreatedAt <= s.AsOf && (!s.CurrentConversationOnly || x.ConversationId == s.ConversationId && (x.MemoryId == null || eligibleMemories.Any(m => m.Id == x.MemoryId)))).ToListAsync(ct);
        var versions = await db.Artifacts.Where(x => x.UserId == s.UserId && x.Namespace == s.Namespace && x.CreatedAt <= s.AsOf && (!s.CurrentConversationOnly || x.ConversationId == s.ConversationId)).ToListAsync(ct);
        bool Matches(string text) => terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
        var matchingArtifacts = versions.Where(a => Matches(a.Name)).Select(a => a.ArtifactId).ToHashSet();
        var result = new
        {
            query,
            scope = s.CurrentConversationOnly ? "current_conversation" : "authorized_user_history",
            temporal,
            strategies = searches.Select(x => new { x.Strategy, status = x.Error == null ? "completed" : "failed", x.Error, count = x.Items.Count }),
            ranking = ranked.Where(x => x.SourceType == "message" ? allowed.Contains(x.SourceId) : memories.Any(m => m.Id == x.SourceId)).Select(x => new { x.SourceId, x.SourceType, x.RetrievalMethod, x.RetrievalScore }),
            memories = memories.Select(m => new { m.Id, text = m.CanonicalText, m.Status, m.Scope, m.ValidFrom, m.ValidTo, sourceIds = links.Where(l => l.MemoryId == m.Id && allowed.Contains(l.SourceId)).Select(l => l.SourceId).Distinct() }),
            sources,
            knowledge = knowledge.Where(k => Matches(k.Name + " " + k.DataJson) || memories.Any(m => m.Id == k.MemoryId)).Take(8).Select(k => new { k.Id, k.Kind, k.Name, k.Status, k.MemoryId, k.ConversationId, data = Json.Node(k.DataJson), evidenceRequired = true }),
            artifacts = versions.Where(a => matchingArtifacts.Contains(a.ArtifactId)).OrderByDescending(a => a.Version).Take(16).Select(a => new { versionId = a.Id, a.ArtifactId, a.Name, a.Version, a.Status, a.CreatedAt }),
            note = "Bounded results, not an exhaustive absence proof. Memory and knowledge summaries are discovery aids; cite original sources. Artifact metadata is not its content."
        };
        return new(Json.Node(Json.Write(result))!.AsObject(), sources);
    }

    public async Task<List<AgentSource>> Load(SearchScope scope, Guid[] ids, bool neighbors, CancellationToken ct, bool bounded = true)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var messages = await Messages(db, scope).Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        if (neighbors)
            foreach (var m in messages.ToArray().Take(8))
                messages.AddRange(await Messages(db, scope).Where(x => x.ConversationId == m.ConversationId && x.SequenceNumber >= m.SequenceNumber - 1 && x.SequenceNumber <= m.SequenceNumber + 1).ToListAsync(ct));
        var conversationIds = messages.Select(m => m.ConversationId).Distinct().ToArray();
        var checkpoints = await db.Conversations.Where(x => conversationIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.LastProcessedSequence, ct);
        var result = messages.DistinctBy(x => x.Id).OrderBy(x => x.CreatedAt).ThenBy(x => x.SequenceNumber).Select(m => new AgentSource(m.Id, "message", m.Content, m.CreatedAt, m.ConversationId, m.SequenceNumber, m.Role, m.SequenceNumber > checkpoints[m.ConversationId])).ToList();
        var versions = await db.Artifacts.Where(x => ids.Contains(x.Id) && x.UserId == scope.UserId && x.Namespace == scope.Namespace && x.CreatedAt <= scope.AsOf && (!scope.CurrentConversationOnly || x.ConversationId == scope.ConversationId)).ToListAsync(ct);
        foreach (var a in versions)
            result.Add(new(a.Id, "artifact_version", await artifacts.ReadAsync(a.StoragePath, ct), a.CreatedAt, a.ConversationId));
        if (!bounded)
            return result; // Server-side reauthorization must not apply retrieval excerpt limits.
        // Preserve requested rows first; never silently substitute an adjacent row.
        var budget = 24000;
        var selectedSources = new List<AgentSource>();
        foreach (var source in result.OrderByDescending(x => ids.Contains(x.Id)))
        {
            if (budget <= 0)
                break;
            var text = source.Content[..Math.Min(source.Content.Length, Math.Min(6000, budget))];
            budget -= text.Length;
            selectedSources.Add(source with { Content = text });
        }

        return selectedSources;
    }
}
