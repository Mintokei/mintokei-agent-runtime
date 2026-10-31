using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mintokei.Memory.Tests;

public sealed class MemoryPostgresFactAttribute : FactAttribute
{
    public MemoryPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEMORY_TEST_POSTGRES")))
            Skip = "Set MEMORY_TEST_POSTGRES to an isolated PostgreSQL/pgvector test database";
    }
}

public sealed class InProcessMemoryTests
{
    [MemoryPostgresFact]
    public async Task Direct_DI_ingestion_is_durable_idempotent_scoped_and_does_not_call_an_LLM()
    {
        var root = Path.Combine(Path.GetTempPath(), "memory-di-test-" + Guid.NewGuid());
        var source = "di-test-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Memory:Root"] = root }).Build());
        services.AddPostgresMemory(Environment.GetEnvironmentVariable("MEMORY_TEST_POSTGRES")!);
        services.Configure<MemoryServiceOptions>(o => o.SourceSystem = source);
        await using var provider = services.BuildServiceProvider();
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await DatabaseSetup.InitializeAsync(db, root);
            // No ILlmProvider is registered: ingestion/retrieval must not resolve or execute one.
            var memory = provider.GetRequiredService<IMemoryService>();
            var message = new MemoryMessage(Guid.NewGuid(), "user", "My test project is Maple-84.", DateTimeOffset.UtcNow, "user");
            var turn = new CompletedMemoryTurn(source, Guid.NewGuid(), Guid.NewGuid(), "DI test", null, "completed", [message]);
            var receipts = await Task.WhenAll(memory.StoreTurnAsync(turn), memory.StoreTurnAsync(turn));
            Assert.Single(receipts, r => r.Duplicate);
            Assert.All(receipts, r => Assert.Equal("pending", r.Status));
            var id = receipts[0].ConversationId;
            Assert.Equal(1, await db.Messages.CountAsync(m => m.ConversationId == id));
            Assert.Equal(1, await db.Jobs.CountAsync(j => j.ConversationId == id));
            Assert.Equal(0, await db.MemorySources.CountAsync(s => s.SourceId == MemoryIdentity.SourceId(source, message.Id)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => memory.StoreTurnAsync(turn with { Messages = [message with { Content = "Changed content" }] }));
            var sourceId = MemoryIdentity.SourceId(source, message.Id);
            var loaded = await memory.LoadEvidenceAsync([sourceId]);
            Assert.Contains("Maple-84", loaded.GetRawText());
            Assert.Contains(message.Id.ToString(), loaded.GetRawText());

            var other = new InProcessMemoryService(factory, provider.GetRequiredService<AgentMemoryTools>(),
                provider.GetRequiredService<SettingsService>(), Options.Create(new MemoryServiceOptions { SourceSystem = source, OwnerId = "other" }));
            Assert.Empty((await other.LoadEvidenceAsync([sourceId])).GetProperty("sources").EnumerateArray());
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.StoreTurnAsync(turn));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
