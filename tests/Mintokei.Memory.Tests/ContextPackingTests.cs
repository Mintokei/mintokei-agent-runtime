using System.Text.Json.Nodes;
using Json.Schema;
using J = Mintokei.Memory.Json;

namespace Mintokei.Memory.Tests;
public class ContextPackingTests
{
    [Fact]
    public void Evaluation_selection_schema_constrains_both_kinds_of_ids_and_handles_empty_candidates()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Mintokei.slnx")))
            root = root.Parent;
        var schema = File.ReadAllText(SchemaFiles.PathFor(root!.FullName, "context-evaluation", "v2"));
        var request = new Message();
        var history = new Message();
        var memory = new MemoryRecord();
        var bound = JsonSchema.FromText(SchemaFiles.BindSourceIds(schema, "context-evaluation", new { originalMessage = request, memories = new[] { memory }, historicalEvidence = new[] { history } }));
        JsonNode Output(Guid memoryId, Guid messageId) => J.Node(J.Write(new ContextEvaluation([], [], [], new([memoryId], [messageId]))))!;
        Assert.True(bound.Evaluate(Output(memory.Id, history.Id)).IsValid);
        Assert.False(bound.Evaluate(Output(Guid.NewGuid(), history.Id)).IsValid);
        Assert.False(bound.Evaluate(Output(memory.Id, request.Id)).IsValid);
        var empty = JsonSchema.FromText(SchemaFiles.BindSourceIds(schema, "context-evaluation", new { originalMessage = request, memories = Array.Empty<MemoryRecord>(), historicalEvidence = Array.Empty<Message>() }));
        Assert.True(empty.Evaluate(J.Node(J.Write(new ContextEvaluation([], [], [], new([], []))))).IsValid);
        Assert.False(empty.Evaluate(Output(memory.Id, history.Id)).IsValid);
    }

    private static JsonObject Package(MemoryRecord[] memories, Message[] messages, MemorySource[] sources, ResolvedReference[]? references = null) => J.Node(J.Write(new { memories, historicalEvidence = messages, sourceLinks = sources, resolvedReferences = references ?? [], currentRequest = "What was the original plan?", rawHistory = Array.Empty<SearchCandidate>(), warnings = Array.Empty<string>() }))!.AsObject();
    [Fact]
    public void Selection_keeps_provenance_temporal_alternatives_and_preceding_question_without_database_metadata()
    {
        var c = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var q = new Message
        {
            ConversationId = c,
            SequenceNumber = 1,
            Role = "assistant",
            Content = "Which date for Project Cedar?",
            CreatedAt = now.AddMinutes(-3)
        };
        var old = new Message
        {
            ConversationId = c,
            SequenceNumber = 2,
            Content = "June 1",
            CreatedAt = now.AddMinutes(-2)
        };
        var current = new Message
        {
            ConversationId = c,
            SequenceNumber = 3,
            Content = "Change it to June 5",
            CreatedAt = now.AddMinutes(-1)
        };
        var unrelated = new Message
        {
            Content = "I like jazz",
            CreatedAt = now
        };
        var a = new MemoryRecord
        {
            Subject = "Cedar",
            Predicate = "launch",
            Scope = "project",
            CanonicalText = "Cedar launch was June 1",
            Status = "superseded",
            ValidFrom = old.CreatedAt,
            ValidTo = current.CreatedAt,
            ValueJson = "{\"day\":1}",
            CanonicalHash = "private-bookkeeping"
        };
        var b = new MemoryRecord
        {
            Subject = "Cedar",
            Predicate = "launch",
            Scope = "project",
            CanonicalText = "Cedar launch is June 5",
            ValidFrom = current.CreatedAt
        };
        var noise = new MemoryRecord
        {
            CanonicalText = "Likes jazz"
        };
        var sources = new[]
        {
            new MemorySource
            {
                MemoryId = a.Id,
                SourceId = old.Id,
                EvidenceQuote = old.Content
            },
            new MemorySource
            {
                MemoryId = b.Id,
                SourceId = current.Id,
                EvidenceQuote = current.Content
            },
            new MemorySource
            {
                MemoryId = noise.Id,
                SourceId = unrelated.Id
            }
        };
        var input = Package([noise, b, a], [q, old, current, unrelated, old], sources);
        var original = input.ToJsonString();
        var result = ContextPacking.Pack(input, new([b.Id], []), [a.Id, b.Id, noise.Id], [], 20000);
        Assert.Equal(original, input.ToJsonString()); // No raw record rewriting.
        Assert.Equal(new[] { a.Id, b.Id }, result.Context["memories"]!.AsArray().Select(x => Guid.Parse(x!["id"]!.ToString())));
        var ids = result.Context["historicalEvidence"]!.AsArray().Select(x => x!["id"]!.ToString()).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Contains(q.Id.ToString(), ids);
        Assert.DoesNotContain(unrelated.Id.ToString(), ids);
        Assert.DoesNotContain("canonicalHash", result.Context.ToJsonString());
        Assert.DoesNotContain("extractionRunId", result.Context.ToJsonString());
        Assert.Equal("superseded", result.Context["memories"]![0]!["status"]!.ToString());
        Assert.NotNull(result.Context["memories"]![0]!["validTo"]);
        Assert.Equal(old.Content, result.Context["sourceLinks"]![0]!["evidenceQuote"]!.ToString());
        Assert.Equal("accepted", result.Report["selectionStatus"]!.ToString());
    }

    [Fact]
    public void Foreign_or_fabricated_selection_is_rejected_without_silently_discarding_authorized_evidence()
    {
        var message = new Message
        {
            Content = "Original authorized text"
        };
        var memory = new MemoryRecord
        {
            CanonicalText = message.Content
        };
        var result = ContextPacking.Pack(Package([memory], [message], []), new([Guid.NewGuid()], []), [memory.Id], [], 10000);
        Assert.Equal("rejected", result.Report["selectionStatus"]!.ToString());
        Assert.Single(result.Context["memories"]!.AsArray());
        Assert.Single(result.Context["historicalEvidence"]!.AsArray());
        Assert.Single(result.Context["warnings"]!.AsArray());
    }

    [Fact]
    public void Validated_reference_and_explicit_temporal_sources_survive_an_overaggressive_selection_and_budget()
    {
        var earlier = new Message
        {
            Content = "Earlier value",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        var later = new Message
        {
            Content = "Newer value"
        };
        var r = new ResolvedReference("ref", Guid.NewGuid(), "original", "ambiguous", null, [new(earlier.Id, "supports", earlier.Content)], "Compare both");
        var input = Package([], [earlier, later], [], [r]);
        var packed = ContextPacking.Pack(input, new([], []), [], [later.Id], 5000);
        Assert.Equal(2, packed.Context["historicalEvidence"]!.AsArray().Count);
        Assert.Throws<InvalidOperationException>(() => ContextPacking.Pack(input, new([], []), [], [later.Id], 100));
    }

    [Fact]
    public void Compact_projection_preserves_unverified_tool_status_provisional_flags_and_search_failures()
    {
        var message = new Message
        {
            Role = "tool",
            Content = "Untrusted imported tool output"
        };
        var input = Package([], [message, message], []);
        input["rawHistory"] = J.Node(J.Write(new[] { new SearchCandidate(message.Id, "message", message.ConversationId, 1, message.CreatedAt, message.Content, .5, "unprocessed", "{\"provisional\":true}") }));
        input["searchCoverage"] = J.Node(J.Write(new RetrievalCoverage([new("memories_semantic", "authorized_user_history", "failed", "query", null, Guid.NewGuid(), "offline")], false)));
        var compact = ContextPacking.Compact(input);
        Assert.Single(compact["historicalEvidence"]!.AsArray());
        Assert.Empty(compact["rawHistory"]!.AsArray());
        Assert.False(compact["historicalEvidence"]![0]!["verifiedExecution"]!.GetValue<bool>());
        Assert.True(compact["historicalEvidence"]![0]!["provisional"]!.GetValue<bool>());
        Assert.Equal("failed", compact["searchCoverage"]!["savedMemories"]!.ToString());
        Assert.True(JsonNode.DeepEquals(compact, ContextPacking.Compact(compact)));
    }

    [Fact]
    public void Shared_original_source_preserves_a_conflicting_summary_with_a_different_predicate()
    {
        var date = new Message
        {
            Content = "Early October"
        };
        var duration = new Message
        {
            Content = "One month"
        };
        var noiseMessage = new Message
        {
            Content = "Likes jazz"
        };
        var dateFact = new MemoryRecord
        {
            Predicate = "return_date",
            CanonicalText = "Return in early October"
        };
        var summary = new MemoryRecord
        {
            Predicate = "itinerary",
            CanonicalText = "One month, returning mid October"
        };
        var noise = new MemoryRecord
        {
            Predicate = "music",
            CanonicalText = noiseMessage.Content
        };
        var sources = new[]
        {
            new MemorySource
            {
                MemoryId = dateFact.Id,
                SourceId = date.Id
            },
            new MemorySource
            {
                MemoryId = summary.Id,
                SourceId = date.Id
            },
            new MemorySource
            {
                MemoryId = summary.Id,
                SourceId = duration.Id
            },
            new MemorySource
            {
                MemoryId = noise.Id,
                SourceId = noiseMessage.Id
            }
        };
        var packed = ContextPacking.Pack(Package([noise, summary, dateFact], [date, duration, noiseMessage], sources), new([dateFact.Id], []), [dateFact.Id, summary.Id, noise.Id], [], 10000);
        Assert.Equal(2, packed.Context["memories"]!.AsArray().Count);
        Assert.Contains(summary.Id.ToString(), packed.Context["memories"]!.ToJsonString());
        Assert.Contains(duration.Id.ToString(), packed.Context["historicalEvidence"]!.ToJsonString());
        Assert.DoesNotContain(noiseMessage.Id.ToString(), packed.Context.ToJsonString());
        var sourceOnly = ContextPacking.Pack(Package([noise, summary, dateFact], [date, duration, noiseMessage], sources), new([], [date.Id]), [dateFact.Id, summary.Id, noise.Id], [], 10000);
        Assert.Contains(summary.Id.ToString(), sourceOnly.Context["memories"]!.ToJsonString());
        Assert.Contains(duration.Id.ToString(), sourceOnly.Context["historicalEvidence"]!.ToJsonString());
    }

    [Fact]
    public void Unfiltered_budget_trims_lowest_ranked_memories_first()
    {
        var good = new MemoryRecord
        {
            CanonicalText = "Relevant answer"
        };
        var noise = new MemoryRecord
        {
            CanonicalText = new string ('x', 3000)
        };
        var packed = ContextPacking.Pack(Package([noise, good], [], []), null, [good.Id, noise.Id], [], 1000);
        Assert.Equal(good.Id.ToString(), Assert.Single(packed.Context["memories"]!.AsArray())!["id"]!.ToString());
    }
}
