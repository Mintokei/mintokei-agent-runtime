using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mintokei.Memory.Postgres;

/// <summary>In-process ingestion and retrieval. Ownership is supplied by the host, never by model arguments.</summary>
public sealed class InProcessMemoryService(
    IDbContextFactory<MemoryDbContext> factory,
    AgentMemoryTools tools,
    SettingsService settings,
    IOptions<MemoryServiceOptions> options) : IMemoryService
{
    public async Task<MemoryIngestionReceipt> StoreTurnAsync(CompletedMemoryTurn input, CancellationToken ct = default)
    {
        if (!MemoryTurnValidation.IsValid(input, options.Value.SourceSystem))
            throw new ArgumentException("Invalid completed-turn envelope", nameof(input));
        _ = RetrievalPolicy.IsTaskScoped(input.SearchScope);
        var conversationId = MemoryIdentity.SourceId(input.SourceSystem, input.TaskId);
        var turnId = MemoryIdentity.SourceId(input.SourceSystem, input.TurnId);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({conversationId.ToString()}))", ct);
        var hash = TraceRecorder.Hash(Json.Write(input));
        var key = $"external:{input.SourceSystem}:{input.TurnId}";
        var owner = options.Value.OwnerId;
        var memoryNamespace = options.Value.Namespace;
        var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is not null && (conversation.UserId != owner || conversation.Namespace != memoryNamespace))
            throw new InvalidOperationException("Source identity collision");
        var prior = await db.Jobs.SingleOrDefaultAsync(j => j.Key == key, ct);
        if (prior != null)
        {
            if (Json.Node(prior.PayloadJson)?["contentHash"]?.ToString() != hash)
                throw new InvalidOperationException("Turn already ingested with different contents");
            return new MemoryIngestionReceipt(conversationId, turnId, prior.Status, true);
        }

        if (conversation == null)
        {
            conversation = new Conversation
            {
                Id = conversationId,
                UserId = options.Value.OwnerId,
                Namespace = options.Value.Namespace,
                Title = input.Title,
                MetadataJson = Json.Write(new { sourceSystem = input.SourceSystem, taskId = input.TaskId, workspaceId = input.WorkspaceId })
            };
            db.Conversations.Add(conversation);
        }
        else if (conversation.UserId != (options.Value.OwnerId) || conversation.Namespace != (options.Value.Namespace) || Json.Node(conversation.MetadataJson)?["sourceSystem"]?.ToString() != input.SourceSystem)
            throw new InvalidOperationException("Source identity collision");
        var sourceIds = input.Messages.Select(m => MemoryIdentity.SourceId(input.SourceSystem, m.Id)).ToArray();
        if (await db.Messages.AnyAsync(m => sourceIds.Contains(m.Id), ct))
            throw new InvalidOperationException("A source message already belongs to an ingested turn");
        foreach (var m in input.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id))
        {
            // Agent-to-agent user-role inputs must never masquerade as a human instruction.
            var role = m.Role == "user" && m.Sender != "user" ? "system" : m.Role;
            db.Messages.Add(new Message { Id = MemoryIdentity.SourceId(input.SourceSystem, m.Id), ConversationId = conversationId, TurnId = turnId, SequenceNumber = ++conversation.VisibleSequence, Role = role, Content = m.Content, CreatedAt = m.CreatedAt, MetadataJson = Json.Write(new { sourceSystem = input.SourceSystem, originalMessageId = m.Id, taskId = input.TaskId, workspaceId = input.WorkspaceId, turnStatus = input.Status, sender = m.Sender, originalMetadata = m.Metadata }) });
        }

        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        db.Jobs.Add(new Job { ConversationId = conversationId, TurnId = turnId, ThroughSequence = conversation.VisibleSequence, Kind = "extract", Key = key, PayloadJson = Json.Write(new { contentHash = hash, input.Status, input.SearchScope }) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new MemoryIngestionReceipt(conversationId, turnId, "pending", false);
    }

    public async Task<JsonElement> SearchAsync(MemorySearch input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Query) || input.Query.Length > 500)
            throw new ArgumentException("Use a query of 1–500 characters", nameof(input));
        var scope = Scope(input.Scope, input.CurrentTaskId);
        var configuration = await settings.Get(ct: ct);
        configuration.TopK = Math.Clamp(input.Limit, 1, 12);
        var result = await tools.Execute("search_knowledge", new JsonObject
        {
            ["query"] = input.Query,
            ["scope"] = "all",
            ["temporal"] = "relevance"
        }, scope, configuration, ct);
        await AddOriginsAsync(result.Data, scope, ct);
        return JsonSerializer.SerializeToElement(result.Data);
    }

    public async Task<JsonElement> LoadEvidenceAsync(MemoryEvidenceRequest request, CancellationToken ct = default)
    {
        if (request.SourceIds is not { Count: > 0 and <= 8 })
            throw new ArgumentException("Supply 1–8 source IDs", nameof(request));
        var scope = Scope(request.Scope, request.CurrentTaskId);
        var sources = await tools.Load(scope, request.SourceIds.ToArray(), true, ct);
        var result = Json.Node(Json.Write(new { sources }))!.AsObject();
        await AddOriginsAsync(result, scope, ct);
        return JsonSerializer.SerializeToElement(result);
    }

    private SearchScope Scope(MemorySearchScope requested, Guid? taskId)
    {
        var local = RetrievalPolicy.IsTaskScoped(requested);
        if (local && (taskId is null || taskId == Guid.Empty))
            throw new ArgumentException("CurrentTask scope requires a non-empty current task ID", nameof(taskId));
        var conversationId = taskId is { } id && id != Guid.Empty
            ? MemoryIdentity.SourceId(options.Value.SourceSystem, id) : Guid.Empty;
        return new(options.Value.OwnerId, options.Value.Namespace, conversationId, long.MaxValue, DateTimeOffset.UtcNow, local);
    }

    private async Task AddOriginsAsync(JsonObject result, SearchScope scope, CancellationToken ct)
    {
        var sources = result["sources"]?.AsArray();
        if (sources == null)
            return;
        var ids = sources.Select(s => Guid.TryParse(s?["id"]?.ToString(), out var id) ? id : Guid.Empty).ToArray();
        await using var db = await factory.CreateDbContextAsync(ct);
        var metadata = await AgentMemoryTools.Messages(db, scope).Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.MetadataJson, ct);
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
