using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Mintokei.Memory.Hosting;
// Server-to-server adapter. Original messages remain owned by the source app; stable mapped IDs
// and origin metadata let the existing extractor, search, provenance, replay and inspector reuse them.
public static class SharedMemoryEndpoints
{
    public record SourceMessage(Guid Id, string Role, string Content, DateTimeOffset CreatedAt, string Sender, string? Metadata);
    public record SourceTurn(string SourceSystem, Guid TaskId, Guid TurnId, string Title, Guid? WorkspaceId, string Status, List<SourceMessage> Messages);
    public record SearchInput(string Query, int Limit = 8);
    public record SourceInput(Guid[] SourceIds);
    public static Guid SourceId(string system, Guid id) => MemoryIdentity.SourceId(system, id);
    public static bool ValidTurn(SourceTurn? input, string expectedSystem) => input is not null && input.SourceSystem == expectedSystem && input.TaskId != Guid.Empty && input.TurnId != Guid.Empty && input.Title is { Length: > 0 and <= 500 } && input.Status is ("completed" or "interrupted" or "failed") && input.Messages is { Count: > 0 and <= 1000 } && input.Messages.All(m => m != null && m.Id != Guid.Empty && m.Content != null && m.Role is ("user" or "assistant" or "tool" or "system") && m.Sender is ("user" or "agent" or "system") && m.CreatedAt != default) && input.Messages.Select(m => m.Id).Distinct().Count() == input.Messages.Count && input.Messages.Sum(m => (long)m.Content.Length) <= 4000000;
    public static void MapSharedMemory(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/memory/v1").AddEndpointFilter(async (context, next) =>
        {
            var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var expected = config["MemoryService:Token"];
            var actual = context.HttpContext.Request.Headers["X-Memory-Token"].ToString();
            if (string.IsNullOrEmpty(expected) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
                return Results.Unauthorized();
            return await next(context);
        });
        group.MapPost("/turns", async (SourceTurn input, IDbContextFactory<MemoryDbContext> factory, IConfiguration config, CancellationToken ct) =>
        {
            if (!ValidTurn(input, config["MemoryService:SourceSystem"] ?? "local-agent"))
                return Results.BadRequest(new { error = "Invalid turn envelope" });
            var conversationId = SourceId(input.SourceSystem, input.TaskId);
            var turnId = SourceId(input.SourceSystem, input.TurnId);
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({conversationId.ToString()}))", ct);
            var hash = TraceRecorder.Hash(Json.Write(input));
            var key = $"external:{input.SourceSystem}:{input.TurnId}";
            var owner = config["MemoryService:OwnerId"] ?? "local";
            var memoryNamespace = config["MemoryService:Namespace"] ?? "live";
            var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId, ct);
            if (conversation is not null && (conversation.UserId != owner || conversation.Namespace != memoryNamespace))
                return Results.Conflict(new { error = "Source identity collision" });
            var prior = await db.Jobs.SingleOrDefaultAsync(j => j.Key == key, ct);
            if (prior != null)
            {
                if (Json.Node(prior.PayloadJson)?["contentHash"]?.ToString() != hash)
                    return Results.Conflict(new { error = "Turn already ingested with different contents" });
                return Results.Ok(new { conversationId, turnId, status = prior.Status, duplicate = true });
            }

            if (conversation == null)
            {
                conversation = new Conversation
                {
                    Id = conversationId,
                    UserId = config["MemoryService:OwnerId"] ?? "local",
                    Namespace = config["MemoryService:Namespace"] ?? "live",
                    Title = input.Title,
                    MetadataJson = Json.Write(new { sourceSystem = input.SourceSystem, taskId = input.TaskId, workspaceId = input.WorkspaceId })
                };
                db.Conversations.Add(conversation);
            }
            else if (conversation.UserId != (config["MemoryService:OwnerId"] ?? "local") || conversation.Namespace != (config["MemoryService:Namespace"] ?? "live") || Json.Node(conversation.MetadataJson)?["sourceSystem"]?.ToString() != input.SourceSystem)
                return Results.Conflict(new { error = "Source identity collision" });
            var sourceIds = input.Messages.Select(m => SourceId(input.SourceSystem, m.Id)).ToArray();
            if (await db.Messages.AnyAsync(m => sourceIds.Contains(m.Id), ct))
                return Results.Conflict(new { error = "A source message already belongs to an ingested turn" });
            foreach (var m in input.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id))
            {
                // Agent-to-agent user-role inputs must never masquerade as a human instruction.
                var role = m.Role == "user" && m.Sender != "user" ? "system" : m.Role;
                db.Messages.Add(new Message { Id = SourceId(input.SourceSystem, m.Id), ConversationId = conversationId, TurnId = turnId, SequenceNumber = ++conversation.VisibleSequence, Role = role, Content = m.Content, CreatedAt = m.CreatedAt, MetadataJson = Json.Write(new { sourceSystem = input.SourceSystem, originalMessageId = m.Id, taskId = input.TaskId, workspaceId = input.WorkspaceId, turnStatus = input.Status, sender = m.Sender, originalMetadata = m.Metadata }) });
            }

            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            db.Jobs.Add(new Job { ConversationId = conversationId, TurnId = turnId, ThroughSequence = conversation.VisibleSequence, Kind = "extract", Key = key, PayloadJson = Json.Write(new { contentHash = hash, input.Status }) });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { conversationId, turnId, status = "pending", duplicate = false });
        });
        group.MapGet("/turns/{sourceTurnId:guid}", async (Guid sourceTurnId, IDbContextFactory<MemoryDbContext> factory, IConfiguration config, CancellationToken ct) =>
        {
            var sourceSystem = config["MemoryService:SourceSystem"] ?? "local-agent";
            var owner = config["MemoryService:OwnerId"] ?? "local";
            var memoryNamespace = config["MemoryService:Namespace"] ?? "live";
            var key = $"external:{sourceSystem}:{sourceTurnId}";
            await using var db = await factory.CreateDbContextAsync(ct);
            var result = await (from job in db.Jobs
                join conversation in db.Conversations on job.ConversationId equals conversation.Id
                where job.Key == key && conversation.UserId == owner && conversation.Namespace == memoryNamespace
                select new { job.Id, job.ConversationId, job.TurnId, job.Status, job.Attempts, job.Error, conversation.LastProcessedSequence })
                .SingleOrDefaultAsync(ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        group.MapPost("/search", async (SearchInput input, AgentMemoryTools tools, SettingsService settings, IDbContextFactory<MemoryDbContext> factory, IConfiguration config, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Query) || input.Query.Length > 500)
                return Results.BadRequest();
            var cfg = await settings.Get();
            cfg.TopK = Math.Clamp(input.Limit, 1, 12);
            var result = await tools.Execute("search_knowledge", new JsonObject { ["query"] = input.Query, ["scope"] = "all", ["temporal"] = "relevance" }, new SearchScope(config["MemoryService:OwnerId"] ?? "local", config["MemoryService:Namespace"] ?? "live", Guid.Empty, long.MaxValue, DateTimeOffset.UtcNow), cfg, ct);
            await AddOrigins(result.Data, factory, config, ct);
            return Results.Ok(result.Data);
        });
        group.MapPost("/sources", async (SourceInput input, AgentMemoryTools tools, IDbContextFactory<MemoryDbContext> factory, IConfiguration config, CancellationToken ct) =>
        {
            if (input.SourceIds is not { Length: > 0 and <= 8 })
                return Results.BadRequest();
            var result = Json.Node(Json.Write(new { sources = await tools.Load(new SearchScope(config["MemoryService:OwnerId"] ?? "local", config["MemoryService:Namespace"] ?? "live", Guid.Empty, long.MaxValue, DateTimeOffset.UtcNow), input.SourceIds, true, ct) }))!.AsObject();
            await AddOrigins(result, factory, config, ct);
            return Results.Ok(result);
        });
    }

    private static async Task AddOrigins(JsonObject result, IDbContextFactory<MemoryDbContext> factory, IConfiguration config, CancellationToken ct)
    {
        var sources = result["sources"]?.AsArray();
        if (sources == null)
            return;
        var ids = sources.Select(s => Guid.TryParse(s?["id"]?.ToString(), out var id) ? id : Guid.Empty).ToArray();
        await using var db = await factory.CreateDbContextAsync(ct);
        var metadata = await AgentMemoryTools.Messages(db, new SearchScope(config["MemoryService:OwnerId"] ?? "local", config["MemoryService:Namespace"] ?? "live", Guid.Empty, long.MaxValue, DateTimeOffset.UtcNow)).Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.MetadataJson, ct);
        foreach (var source in sources.OfType<JsonObject>())
        {
            if (!Guid.TryParse(source["id"]?.ToString(), out var id) || !metadata.TryGetValue(id, out var json))
                continue;
            var origin = Json.Node(json);
            if (origin?["sourceSystem"] == null)
                continue;
            source["origin"] = new JsonObject
            {
                ["sourceSystem"] = origin["sourceSystem"]?.DeepClone(),
                ["messageId"] = origin["originalMessageId"]?.DeepClone(),
                ["taskId"] = origin["taskId"]?.DeepClone(),
                ["workspaceId"] = origin["workspaceId"]?.DeepClone()
            };
        }
    }
}
