using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public static class RetrievalPolicy
{
    public static bool IsExplicitlyLocal(string request) => Regex.IsMatch(request, @"\b(?:(?:only|just)\s+(?:(?:use|search|check|look\s+in|refer\s+to)\s+)?(?:the\s+)?(?:this|current)\s+(?:chat|conversation)|(?:use|search|check)\s+(?:only|just)\s+(?:this|the\s+current)\s+(?:chat|conversation)|(?:this|current)\s+(?:chat|conversation)\s+only|(?:do\s+not|don't)\s+(?:use|search|include)\s+(?:other|older|previous)\s+(?:chats|conversations|memories))\b", RegexOptions.IgnoreCase);
    public static List<ResolvedReference> Latest(IEnumerable<ResolvedReference> references) => references.GroupBy(r => (r.SourceMessageId, r.Expression)).Select(g => g.OrderByDescending(r => r.EvaluatedAt ?? DateTimeOffset.MinValue).First()).ToList();
    public static RetrievalQueryPlan Queries(RetrievalPlan plan, List<ResolvedReference> references, RetrievalCoverage coverage, string request, int maxSearchRounds)
    {
        var queries = plan.Queries.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        if (queries.Count > 0)
            return new(queries, false, "planner_queries");
        var unresolved = references.Where(r => r.Status != "resolved").ToList();
        if (maxSearchRounds <= 0 || unresolved.Count == 0)
            return new([], false, "no_unresolved_reference_or_search_disabled");
        var uncheckedSource = coverage.CurrentConversationOnly ? coverage.CurrentConversationMessages is "not_searched" or "unknown" : coverage.SavedMemories is "not_searched" or "unknown" || coverage.OlderConversationMessages is "not_searched" or "unknown";
        if (!uncheckedSource)
            return new([], false, "allowed_sources_already_attempted");
        queries = unresolved.SelectMany(r => new[] { r.SearchRequest?.Query, r.Expression }).Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        if (queries.Count == 0)
            queries.Add(request);
        return new(queries, true, "unresolved_reference_with_unchecked_sources");
    }
}
