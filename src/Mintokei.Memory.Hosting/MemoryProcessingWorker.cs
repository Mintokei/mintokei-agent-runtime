using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mintokei.Memory.Hosting;

/// <summary>Claims durable memory jobs, serializing each conversation with a renewable lease and advisory lock.</summary>
public sealed class MemoryProcessingWorker(
    IServiceScopeFactory scopes,
    NpgsqlDataSource source,
    ILogger<MemoryProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessNextAsync(stoppingToken))
                    await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Memory worker iteration failed");
                try { await Task.Delay(2000, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task<bool> ProcessNextAsync(CancellationToken stoppingToken)
    {
        var leaseToken = Guid.NewGuid();
        await using var claim = source.CreateCommand("""
            UPDATE "Jobs" SET "Status"='running',"Attempts"="Attempts"+1,
                "LeaseUntil"=now()+interval '90 seconds',"LeaseToken"=@token
            WHERE "Id"=(SELECT j."Id" FROM "Jobs" j
                WHERE ((j."Status"='pending' AND j."AvailableAt"<=now())
                    OR (j."Status"='running' AND j."LeaseUntil"<now()))
                AND j."Kind" IN ('extract','sync','consolidate')
                AND NOT EXISTS(SELECT 1 FROM "Jobs" earlier
                    WHERE earlier."ConversationId"=j."ConversationId" AND earlier."Kind"='extract'
                    AND earlier."ThroughSequence"<j."ThroughSequence"
                    AND earlier."Status" IN ('pending','running','failed') AND j."Kind"='extract')
                ORDER BY CASE j."Kind" WHEN 'extract' THEN 0 WHEN 'sync' THEN 1 ELSE 2 END,j."AvailableAt"
                FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING "Id"
            """);
        claim.Parameters.AddWithValue("token", leaseToken);
        if (await claim.ExecuteScalarAsync(stoppingToken) is not Guid id) return false;

        using var scope = scopes.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        await using var db = await factory.CreateDbContextAsync(stoppingToken);
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, stoppingToken);
        await using var connection = await source.OpenConnectionAsync(stoppingToken);
        await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@key,0))", connection);
        acquire.Parameters.AddWithValue("key", job.ConversationId.ToString());
        if (await acquire.ExecuteScalarAsync(stoppingToken) is not true)
        {
            await db.Jobs.Where(j => j.Id == id && j.LeaseToken == leaseToken).ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, "pending").SetProperty(j => j.Attempts, j => j.Attempts - 1)
                .SetProperty(j => j.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(1))
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null).SetProperty(j => j.LeaseToken, (Guid?)null), stoppingToken);
            return true;
        }

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = RenewLeaseAsync(id, leaseToken, execution, heartbeatStop.Token);
        var status = "completed";
        string? error = null;
        var retryAt = DateTimeOffset.UtcNow;
        var attempts = job.Attempts;
        try
        {
            await scope.ServiceProvider.GetRequiredService<MemoryJobProcessor>().Process(job, execution.Token);
        }
        catch (Exception failure)
        {
            status = attempts >= 3 ? "failed" : "pending";
            error = failure.Message;
            retryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(3, attempts) * 5);
            if (stoppingToken.IsCancellationRequested)
            {
                status = "pending";
                attempts--;
                error = null;
                retryAt = DateTimeOffset.UtcNow;
            }
            // Payloads and prompts belong in the opt-in trace store, not process logs.
            logger.LogWarning("Memory job {JobId} ({Kind}) will be {Status}; failure type {FailureType}",
                job.Id, job.Kind, status, failure.GetType().Name);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            try { await heartbeat; }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
            catch (Exception failure) { logger.LogWarning("Memory lease renewal failed: {FailureType}", failure.GetType().Name); }
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key,0))", connection);
            unlock.Parameters.AddWithValue("key", job.ConversationId.ToString());
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }

        // A stale worker cannot complete or release a lease acquired by another worker.
        var updated = await db.Jobs.Where(j => j.Id == id && j.LeaseToken == leaseToken).ExecuteUpdateAsync(u => u
            .SetProperty(j => j.Status, status).SetProperty(j => j.Error, error)
            .SetProperty(j => j.Attempts, attempts).SetProperty(j => j.AvailableAt, retryAt)
            .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null).SetProperty(j => j.LeaseToken, (Guid?)null), CancellationToken.None);
        if (updated == 0) return true;
        job.Status = status;
        await scope.ServiceProvider.GetRequiredService<IMemoryEvents>().Send(job.ConversationId,
            status == "completed" ? "execution.changed" : "processing.error", new { job.Id, job.TurnId, job.Kind, status, error });
        if (job.Kind == "extract" && status == "completed")
            await scope.ServiceProvider.GetRequiredService<IMemoryJobObserver>().CompletedAsync(job, CancellationToken.None);
        return true;
    }

    private async Task RenewLeaseAsync(Guid id, Guid leaseToken, CancellationTokenSource execution, CancellationToken stoppingToken)
    {
        while (true)
        {
            await Task.Delay(1000, stoppingToken);
            await using var command = source.CreateCommand("""
                UPDATE "Jobs" SET "LeaseUntil"=now()+interval '90 seconds'
                WHERE "Id"=@id AND "LeaseToken"=@token AND "Status"='running'
                """);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("token", leaseToken);
            try
            {
                if (await command.ExecuteNonQueryAsync(stoppingToken) == 0)
                {
                    await execution.CancelAsync();
                    return;
                }
            }
            catch
            {
                await execution.CancelAsync();
                throw;
            }
        }
    }
}
