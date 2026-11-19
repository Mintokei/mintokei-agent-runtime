using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json.Nodes;

namespace Mintokei.Memory.Tests;

public sealed class RetrievalTraceIntegrationTests
{
    [MemoryPostgresFact]
    public async Task Records_real_fuzzy_components_stemming_and_attachment_reasons_without_exposing_future_revisions()
    {
        var root = Path.Combine(Path.GetTempPath(), "trace-integration-" + Guid.NewGuid());
        var owner = "trace-" + Guid.NewGuid();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Memory:Root"] = root }).Build());
        services.AddPostgresMemory(Environment.GetEnvironmentVariable("MEMORY_TEST_POSTGRES")!);
        services.Configure<MemoryServiceOptions>(o => { o.OwnerId = owner; o.SourceSystem = "trace-test"; });
        services.RemoveAll<IEmbeddingProvider>(); services.AddSingleton<IEmbeddingProvider, FixedEmbeddings>();
        await using var provider = services.BuildServiceProvider();
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await DatabaseSetup.InitializeAsync(db, root);
            var memory = provider.GetRequiredService<IMemoryService>();
            var past = DateTimeOffset.UtcNow.AddMinutes(-10);
            var task = Guid.NewGuid();
            var before = new MemoryMessage(Guid.NewGuid(), "user", "Prior context.", past, "user");
            var hit = new MemoryMessage(Guid.NewGuid(), "user", "Quartz histories were downloaded.", past.AddSeconds(1), "user");
            var after = new MemoryMessage(Guid.NewGuid(), "assistant", "Following context.", past.AddSeconds(2), "agent");
            var receipt = await memory.StoreTurnAsync(new("trace-test", task, Guid.NewGuid(), "Trace", null, "completed", [before, hit, after]));
            var scope = new SearchScope(owner, "live", receipt.ConversationId, long.MaxValue, DateTimeOffset.UtcNow, true);
            var hitId = MemoryIdentity.SourceId("trace-test", hit.Id);
            var firstId = MemoryIdentity.SourceId("trace-test", before.Id);
            var fact = new MemoryRecord { UserId = owner, CanonicalText = "Quartz uses saved histories.", CanonicalHash = Guid.NewGuid().ToString(), RecordedAt = past, ValidFrom = past };
            var fuzzy = new MemoryRecord { UserId = owner, CanonicalText = "The user points to the Kunchi project as an example where subscription credentials are obtained and used through a proxy that injects them into request headers on egress.", CanonicalHash = Guid.NewGuid().ToString(), RecordedAt = past, ValidFrom = past };
            db.AddRange(fact, fuzzy);
            db.AddRange(new MemorySource { MemoryId = fact.Id, SourceId = hitId, CreatedAt = past }, new MemorySource { MemoryId = fuzzy.Id, SourceId = hitId, CreatedAt = past });
            await db.SaveChangesAsync();
            var store = provider.GetRequiredService<PostgresMemoryStore>();
            await store.AddAsync(fact, default); await store.AddAsync(fuzzy, default);
            var history = provider.GetRequiredService<HistorySearch>();
            var stemmed = Assert.Single(await history.SearchAsync(scope, "history", 8, "none", default, includeDiagnostics: true));
            var stemMatch = Json.Node(stemmed.MetadataJson)!;
            Assert.True(stemMatch["fullTextMatched"]!.GetValue<bool>());
            Assert.Contains("history", stemMatch["fullTextTerms"]!.ToJsonString());
            Assert.Empty(stemMatch["literalTerms"]!.AsArray());
            const string query = "BTCUSD inverse perpetual download history archives memory OOM resume";
            var fuzzyCandidate = (await history.MemoryLexicalAsync(scope, query, 8, default, includeDiagnostics: true)).Single(c => c.SourceId == fuzzy.Id);
            var match = Json.Node(fuzzyCandidate.MetadataJson)!;
            Assert.False(match["fullTextMatched"]!.GetValue<bool>());
            Assert.Empty(match["literalTerms"]!.AsArray());
            Assert.InRange(match["trigramScore"]!.GetValue<double>(), .0634, .0636);

            var result = await memory.SearchAsync(new("Quartz", Scope: MemorySearchScope.CurrentTask, CurrentTaskId: task, IncludeDiagnostics: true));
            var sources = result.GetProperty("sources").EnumerateArray().ToArray();
            var direct = sources.Single(s => s.GetProperty("id").GetGuid() == hitId);
            var reasons = direct.GetProperty("inclusionReasons").GetRawText();
            Assert.Contains("direct_search_hit", reasons); Assert.Contains("summary_reference", reasons);
            Assert.Contains("neighbor", sources.Single(s => s.GetProperty("id").GetGuid() == firstId).GetProperty("inclusionReasons").GetRawText());
            var traceCandidate = result.GetProperty("retrievalTrace").GetProperty("strategies").EnumerateArray()
                .Single(s => s.GetProperty("strategy").GetString() == "messages").GetProperty("candidates")[0];
            Assert.Equal(hit.Id, traceCandidate.GetProperty("origin").GetProperty("messageId").GetGuid());

            // Current canonical text can be newer than a replay boundary. Never expose its preview or terms in diagnostics.
            db.Add(new MemoryRevision { MemoryId = fact.Id, Version = 1, CreatedAt = past, SnapshotJson = Json.Write(fact) });
            fact.CanonicalText = "FUTURE_PRIVATE_CANONICAL_QUARTZ";
            db.Add(new MemoryRevision { MemoryId = fact.Id, Version = 2, CreatedAt = DateTimeOffset.UtcNow, SnapshotJson = Json.Write(fact) });
            await db.SaveChangesAsync();
            var tools = provider.GetRequiredService<AgentMemoryTools>();
            var replay = await tools.Execute("search_knowledge", new JsonObject { ["query"] = "Quartz", ["scope"] = "all", ["temporal"] = "relevance" },
                scope with { AsOf = past.AddMinutes(1) }, new MemoryConfiguration(), default, includeTrace: true);
            Assert.DoesNotContain("FUTURE_PRIVATE", replay.Data["retrievalTrace"]!.ToJsonString());
            Assert.Contains("details_omitted_scope_or_revision", replay.Data["retrievalTrace"]!.ToJsonString());
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FixedEmbeddings : IEmbeddingProvider
    {
        public string Model => "trace-integration-vectors";
        public int Dimensions => 3;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) => Task.FromResult(new float[] { 1, 0, 0 });
    }
}
