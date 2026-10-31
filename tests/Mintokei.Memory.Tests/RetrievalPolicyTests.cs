namespace Mintokei.Memory.Tests;
public class RetrievalPolicyTests
{
    private static ResolvedReference Unresolved(List<SearchAttempt>? attempts = null) => new("1", Guid.NewGuid(), "our earlier plan", "not_found", null, [], "No evidence in supplied context", new("1", Guid.NewGuid(), "our earlier plan", "earlier plan constraints", "current_conversation", "none"), attempts);
    private static readonly RetrievalPlan Empty = new("task", ["prior constraints"], []);
    [Fact]
    public void Empty_queries_with_unchecked_memory_force_a_bounded_search()
    {
        var attempts = new List<SearchAttempt>
        {
            new("messages", "current_conversation", "completed", "plan", 0, Guid.NewGuid())
        };
        var result = RetrievalPolicy.Queries(Empty, [Unresolved(attempts)], new(attempts, false), "Continue our plan", 1);
        Assert.True(result.UsedFallback);
        Assert.Equal("earlier plan constraints", result.Queries[0]);
        Assert.InRange(result.Queries.Count, 1, 2);
    }

    [Fact]
    public void Old_cached_results_do_not_claim_sources_were_searched()
    {
        var old = Mintokei.Memory.Json.Read<ResolvedReference>("""{"referenceId":"1","sourceMessageId":"00000000-0000-0000-0000-000000000001","expression":"our project","status":"not_found","resolvedValue":null,"evidence":[],"explanation":"No history"}""");
        var coverage = new RetrievalCoverage([], false, true);
        Assert.Equal("unknown", coverage.SavedMemories);
        var result = RetrievalPolicy.Queries(Empty, [old], coverage, "Continue our project", 1);
        Assert.True(result.UsedFallback);
        Assert.Equal("our project", Assert.Single(result.Queries));
    }

    [Fact]
    public void General_questions_and_disabled_fallback_do_not_force_search()
    {
        Assert.Empty(RetrievalPolicy.Queries(new("explain", [], []), [], new([], false), "Explain photosynthesis", 1).Queries);
        Assert.Empty(RetrievalPolicy.Queries(Empty, [Unresolved()], new([], false), "Our plan", 0).Queries);
    }

    [Fact]
    public void Failure_and_partial_success_are_visible_not_reported_as_no_matches()
    {
        var attempts = new List<SearchAttempt>
        {
            new("memories_semantic", "authorized_user_history", "failed", "plan", null, Guid.NewGuid(), "offline")
        };
        Assert.Equal("failed", new RetrievalCoverage(attempts, false).SavedMemories);
        attempts.Add(new("memories_lexical", "authorized_user_history", "completed", "plan", 0, Guid.NewGuid()));
        Assert.Equal("partially_checked", new RetrievalCoverage(attempts, false).SavedMemories);
    }

    [Theory]
    [InlineData("Use only this conversation to answer.")]
    [InlineData("Only search this chat for the plan.")]
    [InlineData("Search the current conversation only.")]
    [InlineData("Don't use older conversations.")]
    public void Explicit_local_restrictions_are_preserved(string request)
    {
        Assert.True(RetrievalPolicy.IsExplicitlyLocal(request));
        var attempts = new List<SearchAttempt>
        {
            new("messages", "current_conversation", "completed", "plan", 0, Guid.NewGuid())
        };
        var coverage = new RetrievalCoverage(attempts, true);
        Assert.Equal("restricted", coverage.SavedMemories);
        Assert.Equal("restricted", coverage.OlderConversationMessages);
        Assert.False(RetrievalPolicy.Queries(Empty, [Unresolved(attempts)], coverage, request, 1).UsedFallback);
    }

    [Theory]
    [InlineData("Find flights for our trip")]
    [InlineData("What did we decide in this chat?")]
    public void A_topic_or_local_reference_does_not_itself_prohibit_broader_lookup(string request) => Assert.False(RetrievalPolicy.IsExplicitlyLocal(request));
    [Fact]
    public void Rechecked_resolution_wins_without_mutating_the_prior_attempt()
    {
        var previous = Unresolved();
        var updated = previous with
        {
            Status = "resolved",
            EvaluatedAt = DateTimeOffset.UtcNow
        };
        Assert.Equal("resolved", Assert.Single(RetrievalPolicy.Latest([previous, updated])).Status);
        Assert.Equal("not_found", previous.Status);
    }
}
