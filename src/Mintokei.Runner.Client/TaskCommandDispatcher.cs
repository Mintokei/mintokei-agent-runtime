using System.Collections.Concurrent;

namespace Mintokei.Runner;

public enum TaskCommandAdmission { Execute, Duplicate, Blocked }

/// <summary>
/// Applies ordered commands independently per process and checkpoints before acknowledging.
/// The server preserves order within each correlation; different streams may interleave.
/// This is replay protection, not a transaction with the operating-system process.
/// </summary>
public sealed class TaskCommandDispatcher(LocalOutbox outbox)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public async Task<bool> DispatchAsync(Guid correlationId, long sequence, Func<Task> execute,
        Func<Task> acknowledge, CancellationToken ct = default)
    {
        var gate = gates.GetOrAdd(correlationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var admission = await outbox.BeginTaskCommandAsync(correlationId, sequence);
            if (admission == TaskCommandAdmission.Blocked) return false;
            if (admission == TaskCommandAdmission.Execute)
            {
                await execute();
                await outbox.CompleteTaskCommandAsync(correlationId, sequence);
            }
            await acknowledge();
            return true;
        }
        finally { gate.Release(); }
    }
}
