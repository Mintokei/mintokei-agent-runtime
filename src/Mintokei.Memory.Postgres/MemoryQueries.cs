namespace Mintokei.Memory.Postgres;

/// <summary>Current-task retrieval excludes summaries with evidence from outside that task.</summary>
public static class MemoryQueries
{
    public static IQueryable<MemoryRecord> ForScope(MemoryDbContext db, SearchScope scope)
    {
        var memories = db.Memories.Where(m => m.UserId == scope.UserId && m.Namespace == scope.Namespace
            && m.RecordedAt <= scope.AsOf && m.ValidFrom <= scope.AsOf);
        if (!scope.CurrentConversationOnly)
            return memories;

        var allowedSources = AgentMemoryTools.Messages(db, scope).Select(m => m.Id);
        return memories.Where(m => db.MemorySources.Any(s => s.MemoryId == m.Id && s.CreatedAt <= scope.AsOf)
            && !db.MemorySources.Any(s => s.MemoryId == m.Id && s.CreatedAt <= scope.AsOf
                && (s.SourceType != "message" || !allowedSources.Contains(s.SourceId))));
    }
}
