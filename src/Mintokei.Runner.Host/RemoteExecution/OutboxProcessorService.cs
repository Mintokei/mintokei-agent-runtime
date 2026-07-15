using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Mintokei.Runner.Host.Persistence;
using Mintokei.Runner.Host.RemoteExecution.Grpc;
using Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner.Host.RemoteExecution;

/// <summary>
/// Background service that dispatches pending outbox messages to connected
/// runners via the per-task gRPC OpenTask stream. When no stream is open
/// for the message's correlation, it asks the runner to open one (via the
/// Control stream's <c>OpenTaskRequest</c> bootstrap) and leaves the
/// message Pending so the next sweep — kicked by the OpenTask handler's
/// <c>NotifyMachineConnected</c> when the stream finally registers — can
/// deliver it.
///
/// Triggered by channel signals when new messages are enqueued or when a
/// runner reconnects.
/// </summary>
public sealed class OutboxProcessorService : BackgroundService
{
    /// <summary>
    /// After this many failed delivery attempts, an outbox message is marked
    /// <see cref="OutboxMessageStatus.Failed"/> and the drain loop continues
    /// past it instead of staying stuck on a single poison message. Each
    /// drain sweep is one attempt.
    /// </summary>
    private const int MaxDeliveryAttempts = 10;

    private readonly Channel<Guid> _workChannel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Per-(machine, correlation) cooldown timestamps for OpenTaskRequest
    /// bootstrap sends. Without this, every drain sweep that finds a
    /// not-yet-open stream re-sends OpenTaskRequest — and every send fires
    /// a fresh runner-side EnsureOpenAsync, which races itself in the
    /// API's OpenTask handler (UNIQUE on RunnerOutboxChannels). Keep one
    /// inflight per correlation by gating on this dictionary.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, Guid), DateTimeOffset> _openTaskRequestSentAt = new();
    private static readonly TimeSpan OpenTaskRequestCooldown = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GrpcTaskChannelRegistry _grpcTaskChannels;
    private readonly GrpcControlChannelRegistry _grpcControlChannels;
    private readonly ILogger<OutboxProcessorService> _logger;

    public OutboxProcessorService(
        IServiceScopeFactory scopeFactory,
        GrpcTaskChannelRegistry grpcTaskChannels,
        GrpcControlChannelRegistry grpcControlChannels,
        ILogger<OutboxProcessorService> logger)
    {
        _scopeFactory = scopeFactory;
        _grpcTaskChannels = grpcTaskChannels;
        _grpcControlChannels = grpcControlChannels;
        _logger = logger;
    }

    public void NotifyNewMessage(Guid machineId)
    {
        _workChannel.Writer.TryWrite(machineId);
    }

    public void NotifyMachineConnected(Guid machineId)
    {
        _workChannel.Writer.TryWrite(machineId);
    }

    public void NotifyDelayedMessage(Guid machineId, DateTimeOffset deliverAt)
    {
        var delay = deliverAt - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            NotifyNewMessage(machineId);
            return;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            NotifyNewMessage(machineId);
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxProcessorService started");

        await foreach (var machineId in _workChannel.Reader.ReadAllAsync(stoppingToken))
        {
            // Drain duplicate signals for the same machine
            while (_workChannel.Reader.TryRead(out _)) { }

            try
            {
                await ProcessMachineOutboxAsync(machineId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error processing outbox for machine {MachineId}", machineId);
            }
        }
    }

    private async Task ProcessMachineOutboxAsync(Guid machineId, CancellationToken ct)
    {
        // No gRPC Control stream means the runner is fully disconnected. We
        // skip this sweep; the Control handshake fires NotifyMachineConnected
        // when the runner reconnects, which re-enters this method.
        if (!_grpcControlChannels.IsOpen(machineId))
        {
            OutboxProcessorLog.MachineNotConnected(_logger, machineId);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();

        var now = DateTimeOffset.UtcNow;
        var messages = await db.OutboxMessages
            .Where(m => m.RunnerMachineId == machineId)
            .Where(m => m.Status == OutboxMessageStatus.Pending || m.Status == OutboxMessageStatus.Sent)
            .Where(m => m.DeliverAfterUtc == null || m.DeliverAfterUtc <= now)
            .Where(m => m.ExpiresAt == null || m.ExpiresAt > now)
            .OrderBy(m => m.SequenceNumber)
            .ToListAsync(ct);

        // Per-correlation head-of-line: if the OpenTask stream for correlation
        // X isn't open we mustn't send a later X message out of order. But
        // OTHER correlations on this machine are independent — we don't want
        // one stuck/orphaned correlation (e.g. a cancelled task whose old
        // correlation has lingering Stdin/Kill messages with no live process)
        // to freeze the entire machine queue. Track blocked correlations per
        // sweep and skip past their later messages instead of breaking.
        var blockedCorrelations = new HashSet<Guid>();
        foreach (var msg in messages)
        {
            if (msg.CorrelationId is Guid corr && blockedCorrelations.Contains(corr))
                continue;

            try
            {
                var sentViaGrpc = await TrySendViaGrpcAsync(machineId, msg, ct);
                if (!sentViaGrpc)
                {
                    // The OpenTask stream for this correlation isn't open yet.
                    // TrySendViaGrpcAsync already asked the runner to open one
                    // (OpenTaskRequest via Control). Mark this correlation
                    // blocked for this sweep so per-correlation ordering is
                    // preserved, but keep draining other correlations. The
                    // OpenTask registration handler fires NotifyMachineConnected
                    // when the stream finally opens, which re-enters this
                    // method and picks up where we left off for that correlation.
                    if (msg.CorrelationId is Guid c) blockedCorrelations.Add(c);
                    continue;
                }

                msg.Status = OutboxMessageStatus.Sent;
                msg.SentAt = DateTimeOffset.UtcNow;
                msg.DeliveryAttempts++;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                msg.DeliveryAttempts++;

                if (msg.DeliveryAttempts >= MaxDeliveryAttempts)
                {
                    // Poison message — quarantine and continue past it so that
                    // one bad message can't block its correlation's queue
                    // forever. Status flips to Failed; the next drain query
                    // (Pending/Sent only) will not pick it up again.
                    msg.Status = OutboxMessageStatus.Failed;
                    await db.SaveChangesAsync(ct);
                    _logger.LogError(ex,
                        "Outbox message {MessageId} (seq {Seq}, type {Type}) marked Failed after {Attempts} attempts to machine {MachineId} — skipping past it",
                        msg.Id, msg.SequenceNumber, msg.MessageType, msg.DeliveryAttempts, machineId);
                    continue;
                }

                // Transient send failure on a stream we believed open. Record
                // the attempt; block this correlation for the rest of the
                // sweep so we don't reorder its later messages. Other
                // correlations keep draining.
                await db.SaveChangesAsync(ct);
                if (msg.CorrelationId is Guid c) blockedCorrelations.Add(c);
                _logger.LogWarning(ex,
                    "Failed to send outbox message {MessageId} (seq {Seq}) to machine {MachineId} (attempt {Attempts}/{Max}) — will retry",
                    msg.Id, msg.SequenceNumber, machineId, msg.DeliveryAttempts, MaxDeliveryAttempts);
            }
        }
    }

    /// <summary>
    /// Attempts to deliver a per-task command (StartProcess / WriteStdin /
    /// KillProcess) directly down the runner's open gRPC OpenTask stream.
    /// Returns true on success; false when no stream is registered for the
    /// (machine, correlation) pair, or when the registry's send call throws.
    ///
    /// On a "no stream open" return, this also sends an
    /// <c>OpenTaskRequest</c> via the Control stream so the runner opens
    /// the missing OpenTask stream — at which point the OpenTask
    /// registration handler kicks the outbox processor to retry this
    /// message. With the SignalR fallback gone, the OpenTaskRequest +
    /// retry-on-stream-open loop is the sole bootstrap mechanism.
    /// </summary>
    private async Task<bool> TrySendViaGrpcAsync(Guid machineId, OutboxMessage msg, CancellationToken ct)
    {
        if (msg.CorrelationId is not Guid correlationId) return false;

        var command = msg.MessageType switch
        {
            OutboxMessageType.StartProcess => new ServerTaskCommand
            {
                Sequence = msg.SequenceNumber,
                Start = new StartProcess { PayloadJson = msg.PayloadJson },
            },
            OutboxMessageType.WriteStdin => new ServerTaskCommand
            {
                Sequence = msg.SequenceNumber,
                Stdin = new WriteStdin { PayloadJson = msg.PayloadJson },
            },
            OutboxMessageType.KillProcess => new ServerTaskCommand
            {
                Sequence = msg.SequenceNumber,
                Kill = new KillProcess { PayloadJson = msg.PayloadJson },
            },
            _ => null,
        };
        if (command is null) return false;

        if (!_grpcTaskChannels.IsOpen(machineId, correlationId))
        {
            // No per-task stream open for this correlation. Ask the runner
            // to open one via the Control stream. Once it does, OpenTask's
            // registration handler kicks the outbox processor; the next
            // sweep delivers this message.
            //
            // Throttle to once per cooldown per (machine, correlation): each
            // OpenTaskRequest triggers a fresh runner-side EnsureOpenAsync,
            // and concurrent ones race in the API's OpenTask handler against
            // the RunnerOutboxChannels (machine, correlation) UNIQUE index.
            // Without throttling, a stuck correlation produces a busy loop
            // of OpenTaskRequest → race → still-not-open → repeat.
            var key = (machineId, correlationId);
            var now = DateTimeOffset.UtcNow;
            var lastSent = _openTaskRequestSentAt.GetValueOrDefault(key);
            if (now - lastSent >= OpenTaskRequestCooldown)
            {
                _openTaskRequestSentAt[key] = now;
                try
                {
                    await _grpcControlChannels.TryRequestTaskStreamOpenAsync(machineId, correlationId, ct);
                }
                catch (Exception ex)
                {
                    OutboxProcessorLog.OpenTaskRequestFailed(_logger, ex, machineId, correlationId);
                }
            }
            return false;
        }

        // Stream is open — clear any cooldown bookkeeping so a future
        // close→reopen cycle isn't gated by a stale timestamp.
        _openTaskRequestSentAt.TryRemove((machineId, correlationId), out _);

        try
        {
            return await _grpcTaskChannels.TrySendAsync(machineId, correlationId, command, ct);
        }
        catch (Exception ex)
        {
            // Treat any send failure as "stream is not viable" — the OpenTask
            // handler's finally block will unregister the broken writer
            // shortly. Leave the message Pending; the next sweep retries.
            _logger.LogWarning(ex,
                "gRPC send failed for outbox message {MessageId} (seq {Seq}, correlation {CorrelationId}) — will retry",
                msg.Id, msg.SequenceNumber, correlationId);
            return false;
        }
    }
}

/// <summary>
/// Source-generated, allocation-free log methods for <see cref="OutboxProcessorService"/>. Keeps the
/// Debug dispatch logs from boxing their <see cref="Guid"/> arguments when Debug is disabled (CA1873);
/// message templates are unchanged.
/// </summary>
internal static partial class OutboxProcessorLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Machine {MachineId} not connected via gRPC, skipping outbox dispatch")]
    public static partial void MachineNotConnected(ILogger logger, Guid machineId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to send OpenTaskRequest to machine {MachineId} for correlation {CorrelationId}")]
    public static partial void OpenTaskRequestFailed(ILogger logger, Exception ex, Guid machineId, Guid correlationId);
}
