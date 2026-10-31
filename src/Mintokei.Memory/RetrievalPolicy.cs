namespace Mintokei.Memory;

public static class RetrievalPolicy
{
    public static bool IsTaskScoped(MemorySearchScope scope) => scope switch
    {
        MemorySearchScope.CurrentTask => true,
        MemorySearchScope.AllAuthorizedHistory => false,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown memory search scope")
    };

    /// <summary>A tool request may narrow the host's boundary, never widen it.</summary>
    public static MemorySearchScope RestrictScope(MemorySearchScope requested, MemorySearchScope allowed)
    {
        var taskOnly = IsTaskScoped(requested);
        var restricted = IsTaskScoped(allowed);
        return taskOnly || restricted ? MemorySearchScope.CurrentTask : MemorySearchScope.AllAuthorizedHistory;
    }

    public static List<ResolvedReference> Latest(IEnumerable<ResolvedReference> references) => references.GroupBy(r => (r.SourceMessageId, r.Expression)).Select(g => g.OrderByDescending(r => r.EvaluatedAt ?? DateTimeOffset.MinValue).First()).ToList();
}
