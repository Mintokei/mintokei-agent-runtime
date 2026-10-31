namespace Mintokei.Memory.Tests;
public class RetrievalPolicyTests
{
    private static ResolvedReference Unresolved(List<SearchAttempt>? attempts = null) => new("1", Guid.NewGuid(), "our earlier plan", "not_found", null, [], "No evidence in supplied context", new("1", Guid.NewGuid(), "our earlier plan", "earlier plan constraints", "current_conversation", "none"), attempts);
    [Theory]
    [InlineData(MemorySearchScope.CurrentTask, MemorySearchScope.AllAuthorizedHistory, MemorySearchScope.CurrentTask)]
    [InlineData(MemorySearchScope.AllAuthorizedHistory, MemorySearchScope.CurrentTask, MemorySearchScope.CurrentTask)]
    [InlineData(MemorySearchScope.AllAuthorizedHistory, MemorySearchScope.AllAuthorizedHistory, MemorySearchScope.AllAuthorizedHistory)]
    public void Explicit_scope_can_be_narrowed_but_not_widened(MemorySearchScope requested, MemorySearchScope allowed, MemorySearchScope expected)
        => Assert.Equal(expected, RetrievalPolicy.RestrictScope(requested, allowed));

    [Fact]
    public void Invalid_scopes_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalPolicy.RestrictScope((MemorySearchScope)99, MemorySearchScope.CurrentTask));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalPolicy.RestrictScope(MemorySearchScope.CurrentTask, (MemorySearchScope)99));
    }

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
