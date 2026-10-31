using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Mintokei.Memory.Postgres;

public class TraceRecorder(IDbContextFactory<MemoryDbContext> factory, IMemoryEvents bus)
{
    public static readonly ActivitySource ActivitySource = new("Mintokei.Memory");
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public async Task<TraceSpan> Start(Conversation c, Guid turn, Guid trace, string operation, object? input, bool capture, Guid? parent = null)
    {
        var span = new TraceSpan
        {
            UserId = c.UserId,
            Namespace = c.Namespace,
            ConversationId = c.Id,
            TurnId = turn,
            TraceId = trace,
            ParentSpanId = parent,
            Operation = operation,
            InputJson = capture ? Json.Write(input) : null
        };
        await using var db = await factory.CreateDbContextAsync();
        db.Traces.Add(span);
        await db.SaveChangesAsync();
        await bus.Send(c.Id, "trace.started", span);
        return span;
    }

    public async Task Save(TraceSpan span)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Traces.Update(span);
        await db.SaveChangesAsync();
    }

    public async Task Finish(TraceSpan span, object? output, bool capture, string? error = null)
    {
        span.OutputJson = capture ? Json.Write(output) : null;
        span.Error = error;
        span.Status = error == null ? "completed" : "failed";
        span.EndedAt = DateTimeOffset.UtcNow;
        span.DurationMs = (span.EndedAt - span.StartedAt).Value.TotalMilliseconds;
        await using var db = await factory.CreateDbContextAsync();
        db.Traces.Update(span);
        await db.SaveChangesAsync();
        await bus.Send(span.ConversationId, "trace.completed", span);
    }
}
