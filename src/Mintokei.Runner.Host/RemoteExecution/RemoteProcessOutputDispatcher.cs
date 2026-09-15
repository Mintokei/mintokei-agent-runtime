using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mintokei.Runner.Host.Persistence;
using Mintokei.Runner.Contracts.Messages;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.Runner.Host.RemoteExecution;

/// <summary>
/// Routes runner-emitted ProcessOutput / ProcessCompleted reports to the
/// in-process pipeline (RemoteProcessHandle channel + ScheduleWakeup
/// detection + delayed-wakeup cleanup + orphan cancellation).
///
/// Both transports call this dispatcher:
///   - SignalR's RunnerHub.ReportProcessOutput / ReportProcessCompleted
///   - gRPC's RunnerLinkService.OpenTask reader loop
///
/// Without this, the gRPC OpenTask uplink path would receive ProcessOutput,
/// persist the cursor, and then drop the data on the floor — the agent
/// execution pipeline (e.g. ClaudeCodeExecutionService waiting for a
/// control_response) would never see anything and time out the handshake.
/// </summary>
public sealed class RemoteProcessOutputDispatcher(
    IServiceScopeFactory scopeFactory,
    RemoteProcessStore remoteProcessStore,
    IRemoteProcessRecovery recoveryService,
    OutboxProcessorService outboxProcessor,
    IRunnerMessageEnqueuer messageEnqueuer,
    IRunnerHost runnerHost,
    ILogger<RemoteProcessOutputDispatcher> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Records an inbound runner message in the per-machine
    /// <see cref="InboundRunnerMessage"/> dedup table and advances
    /// <see cref="RunnerMachine.LastReceivedInboundSequence"/> through any
    /// contiguous run that is now visible in the table. Returns false if the
    /// seq was already recorded (caller should skip dispatch).
    ///
    /// Both transports must call this so dedup is uniform: when the runner
    /// reconnects after an API restart and replays unacked messages, it asks
    /// for the per-machine high-water mark on handshake. If gRPC OpenTask
    /// hasn't been writing to this table, that mark is artificially low —
    /// runner over-replays — duplicates get dispatched a second time
    /// (visible as "agent message once with tools, second time without").
    ///
    /// Concurrency: the row insert and the counter advance are split into two
    /// transactions on purpose. With multiple parallel gRPC streams (OpenTask
    /// per correlation, OpenWatcher, …) two arrivals at counter+1 and counter+2
    /// can race. The old "advance only when sequenceNumber == counter+1" branch
    /// could leave a future seq buffered in the table while the counter stayed
    /// behind, and the gap-fill arrival would then short-circuit at the dedup
    /// check (it's already there from the racy path) — leaving the counter
    /// permanently stuck. The walk runs unconditionally on every successful
    /// insert and uses an ExecuteUpdate with a "&lt;" guard so concurrent
    /// callers can't regress the counter.
    /// </summary>
    public async Task<bool> TryRecordInboundAsync(
        Guid machineId, long sequenceNumber, InboundMessageType messageType, string? payloadJson)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();

        var machine = await db.RunnerMachines.FindAsync(machineId);
        if (machine is null) return false;

        var exists = await db.InboundRunnerMessages
            .AnyAsync(m => m.RunnerMachineId == machineId && m.SequenceNumber == sequenceNumber);
        if (exists)
        {
            RemoteProcessOutputDispatcherLog.DuplicateInboundSkipped(logger, sequenceNumber, machineId);
            return false;
        }

        if (sequenceNumber > machine.LastReceivedInboundSequence + 1)
        {
            // With per-correlation OpenTask streams (post-PR #338) the runner
            // hands out a single monotonic per-machine seq via LocalOutbox,
            // but the API receives messages interleaved across N parallel
            // streams. Out-of-order arrival is the normal case here, not a
            // signal of lost data — the walk-forward below absorbs any
            // future seq once its predecessors land. Kept at Debug because
            // a persistent gap (no walk-forward catch-up over many sweeps)
            // is still useful diagnostic when investigating real loss.
            RemoteProcessOutputDispatcherLog.InboundSequenceGap(
                logger, machineId, machine.LastReceivedInboundSequence + 1, sequenceNumber, machine.LastReceivedInboundSequence);
        }

        // Persist the dedup row in its own transaction so the walk-forward
        // below sees it (and any concurrently-committed rows) when it queries.
        db.InboundRunnerMessages.Add(new InboundRunnerMessage
        {
            Id = Guid.NewGuid(),
            RunnerMachineId = machineId,
            SequenceNumber = sequenceNumber,
            MessageType = messageType,
            PayloadJson = payloadJson,
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // Walk-forward through any contiguous run starting at counter+1 that
        // is now visible. Runs unconditionally — not gated on
        // "sequenceNumber == counter+1" — so a future seq that landed during
        // a parallel-stream race still gets absorbed when the gap fills.
        // Re-read the counter freshly because another concurrent caller may
        // have advanced it past the value we read at the top of the method.
        var currentCounter = await db.RunnerMachines
            .Where(m => m.Id == machineId)
            .Select(m => m.LastReceivedInboundSequence)
            .FirstAsync();
        var candidate = currentCounter;
        while (await db.InboundRunnerMessages
            .AnyAsync(m => m.RunnerMachineId == machineId
                && m.SequenceNumber == candidate + 1))
        {
            candidate++;
        }

        if (candidate > currentCounter)
        {
            // Atomic advance with regression guard: if a concurrent caller
            // already moved the counter beyond `candidate`, this UPDATE is a
            // no-op (rows affected = 0). The higher value wins.
            await db.RunnerMachines
                .Where(m => m.Id == machineId && m.LastReceivedInboundSequence < candidate)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    m => m.LastReceivedInboundSequence, candidate));
        }

        return true;
    }

    public async Task DispatchProcessOutputAsync(Guid machineId, string payloadJson)
    {
        var report = JsonSerializer.Deserialize<ProcessOutputReport>(payloadJson, JsonOptions);
        if (report is null) return;

        var handle = remoteProcessStore.Get(report.CorrelationId)
            ?? await recoveryService.TryRecoverAsync(report.CorrelationId, machineId);

        if (handle is not null)
        {
            var outputType = report.OutputType == "StdErr" ? OutputType.StdErr : OutputType.StdOut;
            await handle.OutputWriter.WriteAsync(new CommandOutput(report.Line, outputType, report.Timestamp));
        }
        else
        {
            // Silent drops here are how one-shot CLIs (branch-name / title gen)
            // appear to "time out at 60s/120s" — the runner produced output and
            // sent ProcessCompleted, but the API had no handle to route it to.
            // Recovery only covers AgentTask correlations, so one-shots are
            // strictly dependent on the in-process Add → Get matching.
            var known = remoteProcessStore.GetAllCorrelationIds();
            logger.LogWarning(
                "DispatchProcessOutput: no handle for correlation {CorrelationId} from machine {MachineId} (output dropped). Known correlations in store: {Count} ({Sample})",
                report.CorrelationId, machineId, known.Count,
                string.Join(",", known.Take(5)));
        }

        await TryHandleScheduleWakeupAsync(machineId, report.CorrelationId, report.Line);
    }

    public async Task DispatchProcessCompletedAsync(Guid machineId, string payloadJson)
    {
        var report = JsonSerializer.Deserialize<ProcessCompletedReport>(payloadJson, JsonOptions);
        if (report is null) return;

        var handle = remoteProcessStore.Get(report.CorrelationId)
            ?? await recoveryService.TryRecoverAsync(report.CorrelationId, machineId);

        if (handle is not null)
        {
            handle.SetExited(report.ExitCode);
            remoteProcessStore.Remove(report.CorrelationId);
        }
        else
        {
            var known = remoteProcessStore.GetAllCorrelationIds();
            logger.LogWarning(
                "DispatchProcessCompleted: no handle for correlation {CorrelationId} from machine {MachineId} (completion dropped — caller's WaitForExitAsync will hang until its own timeout). Known correlations in store: {Count} ({Sample})",
                report.CorrelationId, machineId, known.Count,
                string.Join(",", known.Take(5)));
        }

        await CleanupDelayedWakeupMessagesAsync(report.CorrelationId);
        await runnerHost.OnOrphanCorrelationAsync(report.CorrelationId);
    }

    private async Task TryHandleScheduleWakeupAsync(Guid machineId, Guid correlationId, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.GetProperty("type").GetString() != "assistant") return;
            if (!root.TryGetProperty("message", out var message)) return;
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;

            foreach (var block in content.EnumerateArray())
            {
                if (block.GetProperty("type").GetString() != "tool_use") continue;
                if (block.GetProperty("name").GetString() != "ScheduleWakeup") continue;

                var input = block.GetProperty("input");
                var delaySeconds = input.GetProperty("delaySeconds").GetDouble();
                var prompt = input.GetProperty("prompt").GetString() ?? "";

                await EnqueueOrReplaceDelayedWakeupAsync(machineId, correlationId, delaySeconds, prompt);
                RemoteProcessOutputDispatcherLog.ScheduleWakeupDetected(logger, correlationId, delaySeconds);
                return;
            }
        }
        catch (JsonException) { /* ignore */ }
        catch (KeyNotFoundException) { /* ignore */ }
    }

    private async Task EnqueueOrReplaceDelayedWakeupAsync(
        Guid machineId, Guid correlationId, double delaySeconds, string prompt)
    {
        var deliverAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);

        var stdinJson = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new[] { new { type = "text", text = prompt } },
            },
        }, JsonOptions);

        var payloadObject = new { CorrelationId = correlationId, Text = stdinJson + "\n", AppendNewline = false };

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();

        var existing = await db.OutboxMessages
            .Where(m => m.CorrelationId == correlationId
                && m.MessageType == OutboxMessageType.WriteStdin
                && m.Status == OutboxMessageStatus.Pending
                && m.DeliverAfterUtc != null)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            existing.DeliverAfterUtc = deliverAt;
            existing.PayloadJson = JsonSerializer.Serialize(payloadObject, JsonOptions);
            await db.SaveChangesAsync();
            outboxProcessor.NotifyDelayedMessage(machineId, deliverAt);
            RemoteProcessOutputDispatcherLog.DelayedWakeupReplaced(logger, correlationId, deliverAt);
        }
        else
        {
            await messageEnqueuer.EnqueueAsync(
                machineId,
                OutboxMessageType.WriteStdin,
                payloadObject,
                correlationId: correlationId,
                deliverAfterUtc: deliverAt);
        }
    }

    private async Task CleanupDelayedWakeupMessagesAsync(Guid correlationId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();

        var deleted = await db.OutboxMessages
            .Where(m => m.CorrelationId == correlationId
                && m.MessageType == OutboxMessageType.WriteStdin
                && m.Status == OutboxMessageStatus.Pending
                && m.DeliverAfterUtc != null)
            .ExecuteDeleteAsync();

        if (deleted > 0)
        {
            RemoteProcessOutputDispatcherLog.DelayedWakeupsCleanedUp(logger, deleted, correlationId);
        }
    }
}

/// <summary>
/// Source-generated, allocation-free log methods for <see cref="RemoteProcessOutputDispatcher"/> —
/// keeps the Debug/Information inbound-dispatch logs from boxing their value-type arguments when the
/// level is disabled (CA1873); message templates are unchanged.
/// </summary>
internal static partial class RemoteProcessOutputDispatcherLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping duplicate inbound message seq {Seq} from machine {MachineId}")]
    public static partial void DuplicateInboundSkipped(ILogger logger, long seq, Guid machineId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Inbound sequence gap from machine {MachineId}: expected {Expected}, got {Got}. Counter stays at {Counter} until gap is filled (normal with parallel OpenTask streams).")]
    public static partial void InboundSequenceGap(ILogger logger, Guid machineId, long expected, long got, long counter);

    [LoggerMessage(Level = LogLevel.Information, Message = "ScheduleWakeup detected for correlation {CorrelationId}: delay={DelaySeconds}s")]
    public static partial void ScheduleWakeupDetected(ILogger logger, Guid correlationId, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replaced existing delayed wakeup for correlation {CorrelationId}, new delivery at {DeliverAt}")]
    public static partial void DelayedWakeupReplaced(ILogger logger, Guid correlationId, DateTimeOffset deliverAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleaned up {Count} pending delayed wakeup message(s) for correlation {CorrelationId}")]
    public static partial void DelayedWakeupsCleanedUp(ILogger logger, int count, Guid correlationId);
}
