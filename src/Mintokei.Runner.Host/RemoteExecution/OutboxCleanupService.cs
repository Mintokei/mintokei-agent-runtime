using Microsoft.EntityFrameworkCore;
using Mintokei.Runner.Host.Persistence;

namespace Mintokei.Runner.Host.RemoteExecution;

/// <summary>
/// Periodically cleans up acknowledged outbox messages and expires stale ones.
/// </summary>
public sealed class OutboxCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetainAcknowledgedFor = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxCleanupService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CleanupInterval, stoppingToken);

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
                var now = DateTimeOffset.UtcNow;

                // Delete old acknowledged outbox messages
                var deletedAcked = await db.OutboxMessages
                    .Where(m => m.Status == OutboxMessageStatus.Acknowledged
                        && m.AckedAt < now - RetainAcknowledgedFor)
                    .ExecuteDeleteAsync(stoppingToken);

                // Expire stale undelivered messages
                var expiredCount = await db.OutboxMessages
                    .Where(m => m.ExpiresAt != null && m.ExpiresAt < now
                        && m.Status != OutboxMessageStatus.Acknowledged
                        && m.Status != OutboxMessageStatus.Expired)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.Status, OutboxMessageStatus.Expired),
                        stoppingToken);

                // Clean old inbound audit records (older than 24h)
                var inboundCutoff = now.AddHours(-24);
                var deletedInbound = await db.InboundRunnerMessages
                    .Where(m => m.ReceivedAt < inboundCutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deletedAcked > 0 || expiredCount > 0 || deletedInbound > 0)
                {
                    logger.LogDebug(
                        "Outbox cleanup: deleted {Acked} acked, expired {Expired}, deleted {Inbound} inbound",
                        deletedAcked, expiredCount, deletedInbound);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error during outbox cleanup");
            }
        }
    }
}
