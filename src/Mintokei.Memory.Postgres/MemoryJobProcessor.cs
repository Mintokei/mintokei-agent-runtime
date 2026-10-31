using Microsoft.EntityFrameworkCore;

namespace Mintokei.Memory.Postgres;
/// <summary>Executes durable extraction, consolidation and semantic synchronization jobs.</summary>
public sealed class MemoryJobProcessor(IDbContextFactory<MemoryDbContext> factory, SettingsService settings, MemoryPipeline pipeline, PostgresMemoryStore postgres, Mem0MemoryStore mem0, TraceRecorder traces, IEmbeddingProvider embeddings)
{
    public async Task Process(Job job, CancellationToken ct)
    {
        if (job.Kind == "extract")
        {
            await pipeline.Process(job, ct);
            return;
        }

        if (job.Kind == "consolidate")
        {
            await pipeline.Consolidate(job, ct);
            return;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var c = await db.Conversations.SingleAsync(x => x.Id == job.ConversationId, ct);
        var config = await settings.Get(c, ct);
        if (job.Kind == "sync")
        {
            var payload = Json.Node(job.PayloadJson)!;
            var memoryId = Guid.Parse(payload["memoryId"]!.ToString());
            var memory = await db.Memories.SingleOrDefaultAsync(x => x.Id == memoryId && x.UserId == c.UserId && x.Namespace == c.Namespace, ct);
            if (memory == null)
                return;
            var span = await traces.Start(c, job.TurnId, Guid.NewGuid(), "semantic-store-sync", new { memory.Id, provider = payload["provider"]?.ToString() }, config.CapturePayloads);
            try
            {
                var store = payload["provider"]?.ToString() == "mem0" ? (IMemoryStore)mem0 : postgres;
                var result = await store.AddAsync(memory, ct);
                memory.ProviderMemoryId = result.ProviderId;
                memory.EmbeddingModel = payload["provider"]?.ToString() == "mem0" ? "mem0-local" : embeddings.Model;
                await db.SaveChangesAsync(ct);
                await traces.Finish(span, result, config.CapturePayloads);
            }
            catch (Exception ex)
            {
                await traces.Finish(span, null, config.CapturePayloads, ex.Message);
                throw;
            }

            return;
        }

        throw new ArgumentException("Unsupported memory job kind: " + job.Kind);
    }
}
