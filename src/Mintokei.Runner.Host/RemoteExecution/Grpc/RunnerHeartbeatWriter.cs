using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mintokei.Runner.Host.Persistence;

namespace Mintokei.Runner.Host.RemoteExecution.Grpc;

/// <summary>Bounds SQLite contention on liveness writes without terminating a healthy transport.</summary>
public sealed class RunnerHeartbeatWriter(
    IServiceScopeFactory scopeFactory,
    ILogger<RunnerHeartbeatWriter> logger)
{
    public async Task<bool> TryUpdateAsync(Guid machineId, CancellationToken ct)
    {
        const int attempts = 3;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
                // The normal 30s command timeout blocks the control reader and used to
                // disconnect every task stream on expiry. Heartbeats can retry next tick.
                db.Database.SetCommandTimeout(1);
                await db.RunnerMachines.Where(m => m.Id == machineId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastHeartbeatAt, DateTimeOffset.UtcNow), ct);
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                if (attempt == attempts - 1)
                {
                    logger.LogWarning(ex,
                        "Deferring heartbeat persistence for runner {MachineId} after SQLite contention; control stream remains connected",
                        machineId);
                    return false;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
            }
        }
        return false;
    }
}
