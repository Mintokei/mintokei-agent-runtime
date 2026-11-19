using System.Text.Json.Nodes;

namespace Mintokei.Memory.Tests;

public sealed class RetrievalDiagnosticsTests
{
    private static SearchCandidate Candidate(Guid id, string text = "Quartz", double score = .8) =>
        new(id, "memory", null, null, DateTimeOffset.UtcNow, text, score, "semantic", Json.Write(new { metric = "cosine_similarity", threshold = .30 }));

    [Fact]
    public void Preserves_branch_candidates_contributions_cutoffs_and_evidence_only_returns()
    {
        var a = Candidate(Guid.NewGuid()); var b = Candidate(Guid.NewGuid()); var c = Candidate(Guid.NewGuid());
        var attempts = new[] { ("memories_semantic", new List<SearchCandidate> { a, b, c }, (string?)null),
            ("memories_lexical", new List<SearchCandidate> { b, a }, (string?)null),
            ("messages", new List<SearchCandidate>(), "offline") };
        var trace = Json.Node(Json.Write(RetrievalDiagnostics.Build(attempts, 3, 1,
            new HashSet<Guid> { a.SourceId, b.SourceId, c.SourceId }, new HashSet<Guid> { a.SourceId, c.SourceId })))!;
        var strategies = trace["strategies"]!.AsArray();
        Assert.Equal(3, strategies[0]!["candidates"]!.AsArray().Count);
        Assert.Equal("failed", strategies[2]!["status"]!.ToString());
        var ranking = trace["ranking"]!.AsArray();
        Assert.Equal("returned", ranking[0]!["decision"]!.ToString());
        Assert.Equal("below_fusion_limit", ranking[1]!["decision"]!.ToString());
        Assert.Equal("attached_evidence", ranking[2]!["decision"]!.ToString());
        Assert.Equal(2, ranking[0]!["contributions"]!.AsArray().Count);
        Assert.Equal(1d / 61 + 1d / 62, ranking[0]!["score"]!.GetValue<double>(), 12);
        Assert.Equal(1d / 61, strategies[0]!["candidates"]![0]!["rrfContribution"]!.GetValue<double>(), 12);
    }

    [Fact]
    public void Withholds_identity_excerpt_match_terms_and_scores_when_scope_or_revision_cannot_be_verified()
    {
        var c = Candidate(Guid.NewGuid(), "future private text");
        var trace = Json.Node(Json.Write(RetrievalDiagnostics.Build([("memories_semantic", new List<SearchCandidate> { c }, null)],
            8, 16, new HashSet<Guid>(), new HashSet<Guid>())))!;
        var candidate = trace["strategies"]![0]!["candidates"]![0]!;
        Assert.Null(candidate["sourceId"]); Assert.Null(candidate["excerpt"]); Assert.Null(candidate["score"]); Assert.Null(candidate["match"]);
        Assert.Empty(trace["ranking"]!.AsArray());
        Assert.DoesNotContain("future private text", trace.ToJsonString());
        Assert.DoesNotContain(c.SourceId.ToString(), trace.ToJsonString());
    }

    [Fact]
    public void Captures_previews_without_claiming_to_capture_the_full_database()
    {
        var c = Candidate(Guid.NewGuid(), new string('x', 500));
        var trace = Json.Node(Json.Write(RetrievalDiagnostics.Build([("memories_semantic", new List<SearchCandidate> { c }, null)],
            8, 16, new HashSet<Guid> { c.SourceId }, new HashSet<Guid>())))!;
        Assert.Equal(160, trace["strategies"]![0]!["candidates"]![0]!["excerpt"]!.ToString().Length);
        Assert.True(trace["strategies"]![0]!["candidates"]![0]!["excerptTruncated"]!.GetValue<bool>());
        Assert.Equal("evidence_unavailable_or_budget", trace["ranking"]![0]!["decision"]!.ToString());
        Assert.Contains("bounded", trace["coverage"]!.ToString());
    }

    [Fact]
    public void Literal_terms_are_explicitly_substring_overlaps_not_semantic_or_stemmed_matches()
    {
        Assert.Equal(new[] { "download", "memory" }, RetrievalDiagnostics.LiteralTerms("download history memory", "Downloading data uses memory"));
        Assert.Empty(RetrievalDiagnostics.LiteralTerms("history", "histories"));
        Assert.Contains("ES", RetrievalDiagnostics.Terms("ES history"));
        var longQuery = string.Join(" ", Enumerable.Range(0, 70).Select(i => "term" + i));
        Assert.Equal(64, RetrievalDiagnostics.Terms(longQuery).Length);
        Assert.True(RetrievalDiagnostics.TermsTruncated(longQuery));
    }
}
