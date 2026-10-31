using System.Text.Json.Nodes;

namespace Mintokei.Memory.Tests;
public class MemoryRulesTests
{
    private static Message Msg(string text, string role = "user", long sequence = 1) => new()
    {
        Content = text,
        Role = role,
        SequenceNumber = sequence
    };
    [Fact]
    public void Direct_fact_requires_new_exact_evidence()
    {
        var m = Msg("I have 13 years of .NET experience.");
        var c = new Candidate("c", "fact", m.Content, "user", "experience", null, "confirmed", "global", [new(m.Id, "introduced", m.Content)]);
        Assert.Empty(EvidenceValidator.ValidateCandidate(c, new Dictionary<Guid, Message> { { m.Id, m } }, new HashSet<Guid> { m.Id }, false));
    }

    [Fact]
    public void Invented_id_and_quote_are_rejected()
    {
        var m = Msg("I prefer remote work");
        Assert.NotEmpty(EvidenceValidator.Validate([new(Guid.NewGuid(), "supports", m.Content)], new Dictionary<Guid, Message> { { m.Id, m } }));
        Assert.NotEmpty(EvidenceValidator.Validate([new(m.Id, "supports", "I prefer office work")], new Dictionary<Guid, Message> { { m.Id, m } }));
    }

    [Fact]
    public void Historical_overlap_does_not_reextract_old_fact()
    {
        var m = Msg("I have 13 years of .NET experience.");
        Assert.Contains("No new message establishes this candidate", EvidenceValidator.Validate([new(m.Id, "supports", m.Content)], new Dictionary<Guid, Message> { { m.Id, m } }, new HashSet<Guid> { Guid.NewGuid() }, true));
    }

    [Fact]
    public void Assistant_suggestion_is_not_confirmed_user_preference()
    {
        var m = Msg("You should ask for €80,000.", "assistant");
        var c = new Candidate("c", "preference", m.Content, "user", "salary", null, "confirmed", "global", [new(m.Id, "introduced", m.Content)]);
        Assert.Contains("Assistant suggestion is not a confirmed fact", EvidenceValidator.ValidateCandidate(c, new Dictionary<Guid, Message> { { m.Id, m } }, new HashSet<Guid> { m.Id }, false));
    }

    [Fact]
    public void Scoped_decision_and_global_preference_have_distinct_identity()
    {
        var id = Guid.NewGuid();
        var a = new Candidate("c", "preference", "€70,000", "user", "salary", null, "confirmed", "global", [new(id, "introduced", "€70,000")]);
        Assert.NotEqual(MemoryPipeline.CanonicalHash(a), MemoryPipeline.CanonicalHash(a with { Scope = "cv:5" }));
    }

    [Fact]
    public void State_delta_is_immutable_and_validates_sources()
    {
        var before = JsonNode.Parse("{\"topic\":\"CV\",\"options\":[\"first\"]}")!.AsObject();
        var id = Guid.NewGuid();
        var after = StateReducer.Apply(before, [new("set", "topic", JsonValue.Create("Jobs"), [id]), new("add", "options", JsonValue.Create("second"), [id])], new HashSet<Guid> { id });
        Assert.Equal("CV", before["topic"]!.ToString());
        Assert.Equal("Jobs", after["topic"]!.ToString());
        Assert.Equal(2, after["options"]!.AsArray().Count);
        Assert.Throws<InvalidOperationException>(() => StateReducer.Apply(before, [new("set", "topic", null, [Guid.NewGuid()])], new HashSet<Guid> { id }));
    }

    [Theory]
    [InlineData("__proto__.polluted")]
    [InlineData("a/b")]
    [InlineData("$root")]
    [InlineData("a..b")]
    public void State_path_injection_is_rejected(string path)
    {
        var id = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => StateReducer.Apply(new(), [new("set", path, JsonValue.Create("x"), [id])], new HashSet<Guid> { id }));
    }

    [Fact]
    public void Credentials_are_rejected()
    {
        Assert.True(EvidenceValidator.ContainsSecret("api_key=sk-123456789abcdefghijk"));
        Assert.False(EvidenceValidator.ContainsSecret("I prefer PostgreSQL"));
    }

    [Fact]
    public void Rrf_combines_independent_rank_signals_without_losing_them()
    {
        var a = new SearchCandidate(Guid.NewGuid(), "message", null, 1, DateTimeOffset.UtcNow, "A", .9, "semantic");
        var b = a with
        {
            SourceId = Guid.NewGuid(),
            ContentExcerpt = "B"
        };
        var result = HistorySearch.Fuse([new[] { a, b }, new[] { b }], 10);
        Assert.Equal(b.SourceId, result[0].SourceId);
        Assert.Contains("semantic", result[0].MetadataJson);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Normalized_canonical_identity_is_retry_stable()
    {
        var a = new Candidate("first", "fact", "  Remote work  ", "user", "arrangement", null, "confirmed", "global", []);
        Assert.Equal(MemoryPipeline.CanonicalHash(a), MemoryPipeline.CanonicalHash(a with { TemporaryId = "different", Text = "remote work" }));
    }
}
