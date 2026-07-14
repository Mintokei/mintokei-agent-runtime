using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mintokei.Runner.Host.Persistence;

namespace Mintokei.Runner.Host.RemoteExecution;

public sealed class RunnerMessageEnqueuer(
    IServiceScopeFactory scopeFactory,
    OutboxProcessorService outboxProcessor) : IRunnerMessageEnqueuer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Per-machine lock to serialize sequence number assignment across all scopes
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> MachineLocks = new();

    public async Task<long> EnqueueAsync(
        Guid machineId,
        OutboxMessageType type,
        object payload,
        Guid? correlationId = null,
        TimeSpan? ttl = null,
        DateTimeOffset? deliverAfterUtc = null,
        CancellationToken ct = default)
    {
        var machineLock = MachineLocks.GetOrAdd(machineId, _ => new SemaphoreSlim(1, 1));
        await machineLock.WaitAsync(ct);

        try
        {
            // Use a dedicated scope so each enqueue gets a fresh DbContext,
            // avoiding stale tracked entities from concurrent request scopes.
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();

            var machine = await db.RunnerMachines.FindAsync([machineId], ct)
                ?? throw new InvalidOperationException($"Runner machine {machineId} not found.");

            var sequenceNumber = machine.NextOutboundSequence++;

            var message = new OutboxMessage
            {
                Id = Guid.NewGuid(),
                RunnerMachineId = machineId,
                SequenceNumber = sequenceNumber,
                MessageType = type,
                Status = OutboxMessageStatus.Pending,
                PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
                CorrelationId = correlationId,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = ttl.HasValue ? DateTimeOffset.UtcNow + ttl.Value : null,
                DeliverAfterUtc = deliverAfterUtc,
            };

            db.OutboxMessages.Add(message);
            await db.SaveChangesAsync(ct);

            if (deliverAfterUtc.HasValue)
                outboxProcessor.NotifyDelayedMessage(machineId, deliverAfterUtc.Value);
            else
                outboxProcessor.NotifyNewMessage(machineId);

            return sequenceNumber;
        }
        finally
        {
            machineLock.Release();
        }
    }
}
