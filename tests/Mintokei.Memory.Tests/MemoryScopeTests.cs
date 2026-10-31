using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mintokei.Memory.Tests;

public sealed class MemoryScopeTests
{
    [Fact]
    public void Legacy_turn_hash_and_scope_survive_the_contract_upgrade()
    {
        var old = new { SourceSystem = "mintokei", TaskId = Guid.NewGuid(), TurnId = Guid.NewGuid(), Title = "Task", WorkspaceId = (Guid?)null,
            Status = "completed", Messages = new[] { new MemoryMessage(Guid.NewGuid(), "user", "A fact", DateTimeOffset.UtcNow, "user") } };
        var json = Json.Write(old);
        var turn = Json.Read<CompletedMemoryTurn>(json);
        Assert.Equal(MemorySearchScope.AllAuthorizedHistory, turn.SearchScope);
        Assert.Equal(json, Json.Write(turn)); // Existing retry content hashes must remain identical.
        var restricted = turn with { SearchScope = MemorySearchScope.CurrentTask };
        Assert.Equal(MemorySearchScope.CurrentTask, Json.Read<CompletedMemoryTurn>(Json.Write(restricted)).SearchScope);
    }

    [MemoryPostgresFact]
    public async Task Explicit_scope_filters_messages_memories_artifacts_and_evidence_before_ranking()
    {
        var root = Path.Combine(Path.GetTempPath(), "memory-scope-" + Guid.NewGuid());
        var source = "scope-test-" + Guid.NewGuid().ToString("N");
        var owner = "scope-owner-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Memory:Root"] = root }).Build());
        services.AddPostgresMemory(Environment.GetEnvironmentVariable("MEMORY_TEST_POSTGRES")!);
        services.Configure<MemoryServiceOptions>(o => { o.SourceSystem = source; o.OwnerId = owner; });
        // Deterministic vectors test actual pgvector filtering/ranking, without an external model.
        services.RemoveAll<IEmbeddingProvider>();
        services.AddSingleton<IEmbeddingProvider, FixedEmbeddings>();
        await using var provider = services.BuildServiceProvider();
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await DatabaseSetup.InitializeAsync(db, root);
            var memory = provider.GetRequiredService<IMemoryService>();
            var taskA = Guid.NewGuid(); var taskB = Guid.NewGuid();
            var a = new MemoryMessage(Guid.NewGuid(), "user", "Quartz project uses SQLite.", DateTimeOffset.UtcNow.AddMinutes(-2), "user");
            var b = new MemoryMessage(Guid.NewGuid(), "user", "Quartz project uses PostgreSQL.", DateTimeOffset.UtcNow.AddMinutes(-1), "user");
            var receiptA = await memory.StoreTurnAsync(new(source, taskA, Guid.NewGuid(), "A", null, "completed", [a], MemorySearchScope.CurrentTask));
            var receiptB = await memory.StoreTurnAsync(new(source, taskB, Guid.NewGuid(), "B", null, "completed", [b]));
            var idA = MemoryIdentity.SourceId(source, a.Id); var idB = MemoryIdentity.SourceId(source, b.Id);
            var job = await db.Jobs.SingleAsync(j => j.ConversationId == receiptA.ConversationId);
            Assert.Equal("CurrentTask", Json.Node(job.PayloadJson)!["searchScope"]!.ToString());

            MemoryRecord Fact(string value) => new() { UserId = owner, Namespace = "live", CanonicalText = value, CanonicalHash = Guid.NewGuid().ToString() };
            var factA = Fact(a.Content); var factB = Fact(b.Content); var mixed = Fact("Quartz project has a combined cross-task summary.");
            db.AddRange(factA, factB, mixed);
            db.AddRange(new MemorySource { MemoryId = factA.Id, SourceId = idA }, new MemorySource { MemoryId = factB.Id, SourceId = idB },
                new MemorySource { MemoryId = mixed.Id, SourceId = idA }, new MemorySource { MemoryId = mixed.Id, SourceId = idB });
            var artifactStore = provider.GetRequiredService<IArtifactStore>();
            var artA = new ArtifactVersion { UserId = owner, Name = "Quartz local plan", ConversationId = receiptA.ConversationId, ArtifactId = Guid.NewGuid() };
            var artB = new ArtifactVersion { UserId = owner, Name = "Quartz other plan", ConversationId = receiptB.ConversationId, ArtifactId = Guid.NewGuid() };
            artA.StoragePath = await artifactStore.WriteAsync(artA.Id, "Local artifact", default);
            artB.StoragePath = await artifactStore.WriteAsync(artB.Id, "Other artifact", default);
            db.AddRange(artA, artB);
            db.AddRange(new KnowledgeRecord { UserId = owner, Name = "Quartz local goal", ConversationId = receiptA.ConversationId, MemoryId = factA.Id },
                new KnowledgeRecord { UserId = owner, Name = "Quartz other goal", ConversationId = receiptB.ConversationId, MemoryId = factB.Id },
                new KnowledgeRecord { UserId = owner, Name = "Quartz mixed goal", ConversationId = receiptA.ConversationId, MemoryId = mixed.Id });
            await db.SaveChangesAsync();
            var store = provider.GetRequiredService<PostgresMemoryStore>();
            foreach (var fact in new[] { factA, factB, mixed }) await store.AddAsync(fact, default);

            var local = await memory.SearchAsync(new("Quartz", Scope: MemorySearchScope.CurrentTask, CurrentTaskId: taskA));
            Assert.Equal(factA.Id, Assert.Single(local.GetProperty("memories").EnumerateArray()).GetProperty("id").GetGuid());
            Assert.All(local.GetProperty("sources").EnumerateArray(), s => Assert.Equal(receiptA.ConversationId, s.GetProperty("conversationId").GetGuid()));
            Assert.Equal(artA.Id, Assert.Single(local.GetProperty("artifacts").EnumerateArray()).GetProperty("versionId").GetGuid());
            Assert.Equal("Quartz local goal", Assert.Single(local.GetProperty("knowledge").EnumerateArray()).GetProperty("name").GetString());
            Assert.All(local.GetProperty("strategies").EnumerateArray(), s => Assert.Equal("completed", s.GetProperty("status").GetString()));
            var narrow = new SearchScope(owner, "live", receiptA.ConversationId, long.MaxValue, DateTimeOffset.UtcNow, true);
            Assert.Equal(factA.Id, Assert.Single((await store.SearchAsync(new(narrow, "Quartz", 1), default)).Items).SourceId);
            Assert.Equal(factA.Id, Assert.Single(await provider.GetRequiredService<HistorySearch>().MemoryLexicalAsync(narrow, "Quartz", 1, default)).SourceId);

            var all = await memory.SearchAsync(new("Quartz", CurrentTaskId: taskA));
            Assert.Equal(3, all.GetProperty("memories").GetArrayLength());
            var loaded = await memory.LoadEvidenceAsync(new([idA, idB, artA.Id, artB.Id], MemorySearchScope.CurrentTask, taskA));
            var ids = loaded.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToArray();
            Assert.Contains(idA, ids); Assert.Contains(artA.Id, ids); Assert.DoesNotContain(idB, ids); Assert.DoesNotContain(artB.Id, ids);
            Assert.Equal(4, (await memory.LoadEvidenceAsync(new([idA, idB, artA.Id, artB.Id]))).GetProperty("sources").GetArrayLength());
            Assert.Empty((await memory.SearchAsync(new("Quartz", Scope: MemorySearchScope.CurrentTask, CurrentTaskId: Guid.NewGuid()))).GetProperty("sources").EnumerateArray());
            await Assert.ThrowsAsync<ArgumentException>(() => memory.SearchAsync(new("Quartz", Scope: MemorySearchScope.CurrentTask)));
            await Assert.ThrowsAsync<ArgumentException>(() => memory.LoadEvidenceAsync(new([idB], MemorySearchScope.CurrentTask, Guid.Empty)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => memory.SearchAsync(new("Quartz", Scope: (MemorySearchScope)99)));
        }
        finally { Directory.Delete(root, true); }
    }

    [MemoryPostgresFact]
    public async Task Worker_and_reference_validation_use_the_frozen_scope_without_reading_phrases()
    {
        var root = Path.Combine(Path.GetTempPath(), "memory-worker-scope-" + Guid.NewGuid());
        var source = "worker-scope-" + Guid.NewGuid();
        var ns = source;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Memory:Root"] = root }).Build());
        services.AddPostgresMemory(Environment.GetEnvironmentVariable("MEMORY_TEST_POSTGRES")!);
        services.Configure<MemoryServiceOptions>(o => { o.SourceSystem = source; o.Namespace = ns; });
        var llm = new CaptureExtraction();
        services.AddSingleton<IMemoryReasoningRunner>(llm);
        await using var provider = services.BuildServiceProvider();
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await DatabaseSetup.InitializeAsync(db, root);
            var memory = provider.GetRequiredService<IMemoryService>();
            var oldTime = DateTimeOffset.UtcNow.AddMinutes(-10);
            var earlier = new MemoryMessage(Guid.NewGuid(), "user", "Quartz project uses PostgreSQL.", oldTime, "user");
            var other = await memory.StoreTurnAsync(new(source, Guid.NewGuid(), Guid.NewGuid(), "Other task", null, "completed", [earlier]));
            var request = new MemoryMessage(Guid.NewGuid(), "user", "Use the earlier Quartz project.", DateTimeOffset.UtcNow, "user");
            var current = await memory.StoreTurnAsync(new(source, Guid.NewGuid(), Guid.NewGuid(), "Current task", null, "completed", [request], MemorySearchScope.CurrentTask));
            var fact = new MemoryRecord { Namespace = ns, CanonicalText = earlier.Content, CanonicalHash = Guid.NewGuid().ToString(), ValidFrom = oldTime, RecordedAt = oldTime };
            var oldId = MemoryIdentity.SourceId(source, earlier.Id);
            db.Add(fact);
            db.Add(new MemorySource { MemoryId = fact.Id, SourceId = oldId, CreatedAt = oldTime });
            db.Add(new KnowledgeRecord { Namespace = ns, Kind = "goal", Name = "Quartz outside goal", MemoryId = fact.Id, ConversationId = other.ConversationId, CreatedAt = oldTime });
            await db.SaveChangesAsync();
            var job = await db.Jobs.SingleAsync(j => j.ConversationId == current.ConversationId);
            await provider.GetRequiredService<MemoryPipeline>().Process(job, default);
            var input = Assert.Single(llm.Inputs);
            Assert.Equal("CurrentTask", input["allowedSearchScope"]!.ToString());
            Assert.Empty(input["relevantExistingMemories"]!.AsArray());
            Assert.Empty(input["relatedGoals"]!.AsArray());

            var c = await db.Conversations.SingleAsync(c => c.Id == current.ConversationId);
            var m = await db.Messages.SingleAsync(m => m.ConversationId == c.Id);
            var outside = await db.Messages.SingleAsync(m => m.Id == oldId);
            var reference = new ReferenceRequest("ref", m.Id, "the earlier Quartz project", "Quartz", "cross_conversation", "none");
            var proposal = new ResolvedReference("ref", m.Id, reference.Expression, "resolved", Json.Node("{\"database\":\"PostgreSQL\"}"),
                [new(m.Id, "introduced", m.Content), new(outside.Id, "contextualizes", outside.Content)], "Earlier source");
            var resolver = provider.GetRequiredService<ReferenceResolutionService>();
            var rejected = await resolver.ValidateProposalsAsync(c, m, [reference], [proposal], [m, outside], [], [],
                new MemoryConfiguration { SearchScope = MemorySearchScope.CurrentTask }, Guid.NewGuid(), Guid.NewGuid(), default);
            Assert.Equal("not_found", Assert.Single(rejected).Status);
            var accepted = await resolver.ValidateProposalsAsync(c, m, [reference], [proposal], [m, outside], [], [],
                new MemoryConfiguration { SearchScope = MemorySearchScope.AllAuthorizedHistory }, Guid.NewGuid(), Guid.NewGuid(), default);
            Assert.Equal("resolved", Assert.Single(accepted).Status);
            // The cached broad resolution must also be revalidated under a later restricted request.
            var cached = await resolver.ResolveAsync(c, m, [], new System.Text.Json.Nodes.JsonObject(),
                new MemoryConfiguration { SearchScope = MemorySearchScope.CurrentTask }, Guid.NewGuid(), default);
            Assert.Empty(cached);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CaptureExtraction : IMemoryReasoningRunner
    {
        public List<System.Text.Json.Nodes.JsonNode> Inputs { get; } = [];
        public Task<MemoryReasoningResult> RunAsync(MemoryReasoningRequest request, CancellationToken ct)
        {
            Assert.Contains(request.Operation, new[] { "extraction", "reference-detection" });
            if (request.Operation == "extraction") Inputs.Add(Json.Node(request.Prompt.Split('\n', 2)[1])!);
            var output = request.Operation == "extraction"
                ? "{\"memoryCandidates\":[],\"stateChanges\":[],\"unresolvedReferences\":[]}"
                : "{\"references\":[]}";
            return Task.FromResult(new MemoryReasoningResult(true, output, null));
        }

    }

    private sealed class FixedEmbeddings : IEmbeddingProvider
    {
        public string Model => "scope-test-vectors";
        public int Dimensions => 3;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) => Task.FromResult(new float[] { 1, 0, 0 });
    }
}
