using System.Text.RegularExpressions;

namespace Mintokei.Memory.Postgres;

/// <summary>Bounded retrieval observations, not an LLM explanation or a log of the entire database.</summary>
public static class RetrievalDiagnostics
{
    public const int TermLimit = 64;
    private static string[] AllTerms(string query) => Regex.Matches(query, @"[\p{L}\p{N}_.-]+")
        .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string[] Terms(string query) => AllTerms(query).Take(TermLimit).ToArray();
    public static bool TermsTruncated(string query) => AllTerms(query).Length > TermLimit;

    public static string[] LiteralTerms(string query, string text) => Terms(query)
        .Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static object Build(
        IEnumerable<(string Strategy, List<SearchCandidate> Items, string? Error)> attempts,
        int candidateLimit, int fusionLimit, IReadOnlySet<Guid> inspectable, IReadOnlySet<Guid> returned, IReadOnlyDictionary<Guid, string>? exclusions = null)
    {
        var searches = attempts.ToArray();
        var all = HistorySearch.Fuse(searches.Select(s => s.Items), int.MaxValue);
        var ranks = all.Select((c, i) => (c.SourceId, Rank: i + 1)).ToDictionary(x => x.SourceId, x => x.Rank);
        string Decision(Guid id) => ranks[id] > fusionLimit
            ? returned.Contains(id) ? "attached_evidence" : "below_fusion_limit"
            : returned.Contains(id) ? "returned" : exclusions?.GetValueOrDefault(id) ?? "evidence_unavailable_or_budget";
        return new
        {
            version = 1,
            candidateLimit,
            fusionLimit,
            rrfK = 60,
            coverage = "Only candidates returned by bounded search branches are traced; rows below each branch limit or threshold were not captured.",
            strategies = searches.Select(s => new
            {
                strategy = s.Strategy, status = s.Error == null ? "completed" : "failed", error = s.Error,
                count = s.Items.Count,
                candidates = s.Items.Select((c, i) => new
                {
                    // Reauthorization applies even to candidates discarded by fusion. Historical revisions
                    // cannot be explained using current text/terms from after the replay boundary.
                    sourceId = inspectable.Contains(c.SourceId) ? (Guid?)c.SourceId : null,
                    sourceType = c.SourceType, rank = i + 1,
                    score = inspectable.Contains(c.SourceId) ? (double?)c.RetrievalScore : null,
                    method = c.RetrievalMethod,
                    excerpt = inspectable.Contains(c.SourceId) ? c.ContentExcerpt[..Math.Min(c.ContentExcerpt.Length, 160)] : null,
                    excerptTruncated = inspectable.Contains(c.SourceId) && c.ContentExcerpt.Length > 160,
                    match = inspectable.Contains(c.SourceId) ? Json.Node(c.MetadataJson) : null,
                    fusedRank = ranks[c.SourceId], rrfContribution = 1.0 / (60 + i + 1),
                    decision = inspectable.Contains(c.SourceId) ? Decision(c.SourceId) : "details_omitted_scope_or_revision"
                })
            }),
            ranking = all.Where(c => inspectable.Contains(c.SourceId)).Select(c => new
            {
                c.SourceId, c.SourceType, rank = ranks[c.SourceId], score = c.RetrievalScore,
                selectedByFusion = ranks[c.SourceId] <= fusionLimit, returned = returned.Contains(c.SourceId), decision = Decision(c.SourceId),
                contributions = searches.SelectMany(s => s.Items.Select((item, i) => (Item: item, Rank: i + 1, s.Strategy)))
                    .Where(x => x.Item.SourceId == c.SourceId)
                    .Select(x => new { x.Strategy, x.Rank, rawScore = x.Item.RetrievalScore, rrfContribution = 1.0 / (60 + x.Rank) })
            })
        };
    }
}
