using Google.Protobuf.WellKnownTypes;
using global::Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mintokei.AgentControlPlane;
using Mintokei.Runner.Host.Domain.Machines;
using Mintokei.Runner.Host.Domain.Machines.Enums;
using Mintokei.Runner.Host.Persistence;
using Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner.Host.RemoteExecution.Grpc;

/// <summary>
/// gRPC service that runners connect to. All five RPCs (Control, OpenWatcher,
/// OpenTask, OpenQuery, OpenBulk) are wired and routing.
///
/// The Control handshake performs the same side-effects as SignalR's
/// RunnerHub.Handshake (machine status, outbox ack, reconciliation, file
/// watcher restart). Both transports run in parallel during the SignalR
/// removal series; converging writes are idempotent so dual-running is safe.
/// </summary>
public sealed class RunnerLinkService(
    IServiceScopeFactory scopeFactory,
    GrpcTaskChannelRegistry taskChannelRegistry,
    GrpcWatcherChannelRegistry watcherChannelRegistry,
    GrpcQueryChannelRegistry queryChannelRegistry,
    GrpcBulkChannelRegistry bulkChannelRegistry,
    GrpcControlChannelRegistry controlChannelRegistry,
    PendingQueryStore pendingQueryStore,
    RemoteProcessOutputDispatcher processOutputDispatcher,
    OutboxProcessorService outboxProcessor,
    IRunnerHost runnerHost,
    IOptions<RunnerHostOptions> runnerHostOptions,
    RunnerFileServerPortStore fileServerPortStore,
    RemoteProcessStore remoteProcessStore,
    IRunnerRegistry runnerRegistry,
    ILogger<RunnerLinkService> logger) : RunnerLink.RunnerLinkBase
{
    // This Control stream owns runner presence (RunnerMachine.Status / IsRunnerConnected): it registers
    // the runner in the control plane on open (under a per-stream connection id) and runs the full
    // disconnect teardown on close. (The legacy SignalR RunnerHub that used to own presence is gone.)
    public override async Task Control(
        IAsyncStreamReader<RunnerControlMessage> requestStream,
        IServerStreamWriter<ServerControlMessage> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;

        // First message must be Handshake.
        if (!await requestStream.MoveNext(ct))
            return;

        var first = requestStream.Current;
        if (first.PayloadCase != RunnerControlMessage.PayloadOneofCase.Handshake)
        {
            await responseStream.WriteAsync(new ServerControlMessage
            {
                Handshake = new HandshakeResponse
                {
                    Success = false,
                    Error = "First message on Control stream must be HandshakeRequest",
                },
            }, ct);
            return;
        }

        // Identify the runner from the JWT machine_id claim.
        var user = context.GetHttpContext().User;
        var machineIdClaim = user.FindFirst("machine_id")?.Value;
        if (machineIdClaim is null || !Guid.TryParse(machineIdClaim, out var machineId))
        {
            await responseStream.WriteAsync(new ServerControlMessage
            {
                Handshake = new HandshakeResponse
                {
                    Success = false,
                    Error = "Machine identity not found in token",
                },
            }, ct);
            return;
        }

        // Per-stream connection id for presence. gRPC has no long-lived connection id like SignalR's
        // Context.ConnectionId, so we mint one per Control stream. The control plane's tracker is keyed
        // on it exactly like a SignalR connection id, which preserves the stale-evict race-safety: a
        // reconnect registers a NEW id and evicts this one, so a late close of the old stream can't tear
        // down the runner that already came back. The "grpc:" prefix keeps it distinct from SignalR ids.
        var controlConnectionId = "grpc:" + Guid.NewGuid().ToString("N");

        // Run the same side-effects SignalR's RunnerHub.Handshake performs:
        // mark machine Online, refresh ConnectedAt + LastHeartbeatAt, advance
        // the per-machine outbound ack, flip OutboxMessages up to that ack
        // to Acknowledged so the cleanup service can delete them, register
        // the runner's file-server port for tunnel previews, kick the outbox
        // processor to drain anything that queued during the disconnect,
        // reconcile the runner's reported active correlations against DB
        // tasks (kill orphans, mark dead-on-runner tasks Cancelled), and
        // restart any file watchers active subscribers were waiting on.
        //
        // SignalR runs the same logic in parallel today; both transports
        // converging on identical writes is fine (last write wins, ack
        // marks are idempotent). After SignalR is removed, this becomes
        // the only handshake side-effect path.
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
            var machine = await db.RunnerMachines.FindAsync(new object?[] { machineId }, ct);
            if (machine is null)
            {
                await responseStream.WriteAsync(new ServerControlMessage
                {
                    Handshake = new HandshakeResponse
                    {
                        Success = false,
                        Error = "Unknown machine",
                    },
                }, ct);
                return;
            }

            machine.ConnectedAt = DateTimeOffset.UtcNow;
            machine.LastHeartbeatAt = DateTimeOffset.UtcNow;
            machine.LastAckedOutboundSequence = Math.Max(
                machine.LastAckedOutboundSequence, first.Handshake.LastAckedOutboundSequence);

            await db.SaveChangesAsync(ct);

            await db.OutboxMessages
                .Where(m => m.RunnerMachineId == machine.Id
                    && m.SequenceNumber <= first.Handshake.LastAckedOutboundSequence
                    && m.Status != OutboxMessageStatus.Acknowledged)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, OutboxMessageStatus.Acknowledged)
                    .SetProperty(m => m.AckedAt, DateTimeOffset.UtcNow), ct);
        }

        fileServerPortStore.Register(machineId, first.Handshake.FileServerPort);

        // Register the Control writer BEFORE kicking the outbox so that the
        // first drain after handshake can send OpenTaskRequest bootstrap
        // signals through it. Without this register-first order, that drain
        // races the writer registration: with the SignalR fallback removed
        // (PR 5+) any per-task message queued during the disconnect would
        // sit Pending until the next NotifyMachineConnected happens to fire
        // — which doesn't happen in steady state once the connect-time
        // signal has already passed.
        controlChannelRegistry.Register(machineId, responseStream);

        // Presence: THIS stream owns RunnerMachine.Status — register the runner in the control plane under
        // our per-stream connection id so the sidecar projects Online.
        runnerRegistry.ConnectRunner(machineId, controlConnectionId);

        outboxProcessor.NotifyMachineConnected(machineId);

        // Reconcile reported correlations vs DB tasks (best-effort, errors logged).
        var reportedCorrelations = first.Handshake.ActiveCorrelationIds
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToList();
        try
        {
            await runnerHost.OnRunnerConnectedAsync(machineId, reportedCorrelations);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "gRPC handshake reconciliation failed for machine {MachineId}", machineId);
        }

        // NOTE: file-watcher restart was previously triggered here, but at
        // handshake time the runner has not yet opened the OpenWatcher
        // stream — RestartWatchersForMachineAsync would no-op silently with
        // the gRPC channel registry empty. The restart is now triggered
        // from <see cref="OpenWatcher"/> at the moment the watcher stream
        // registers, which is the only point where the send is guaranteed
        // to land.

        var handshakeResponse = new HandshakeResponse
        {
            MachineId = machineId.ToString(),
            Success = true,
        };

        // Deliver the CLI probe specs on the Control stream so the runner discovers + reports its
        // installed CLIs (it replies with an InstalledClisReport on this stream). Best-effort; a
        // probe-list failure must not fail the handshake.
        try
        {
            foreach (var spec in await runnerHostOptions.Value.CliProbesProvider(ct))
            {
                handshakeResponse.CliProbes.Add(new CliProbeRequest
                {
                    AgentToolKey = spec.AgentToolKey,
                    BinaryName = spec.BinaryName,
                    VersionArgs = spec.VersionArgs,
                    VersionRegex = spec.VersionRegex ?? string.Empty,
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "gRPC handshake failed to load CLI probe specs for machine {MachineId}", machineId);
        }

        await responseStream.WriteAsync(new ServerControlMessage { Handshake = handshakeResponse }, ct);

        logger.LogInformation(
            "Runner {MachineId} connected via gRPC Control stream (lastAckedOutbound={LastAcked}, activeCorrelations={Count}, cliProbes={Probes})",
            machineId, first.Handshake.LastAckedOutboundSequence, reportedCorrelations.Count, handshakeResponse.CliProbes.Count);

        try
        {
            while (await requestStream.MoveNext(ct))
            {
                var msg = requestStream.Current;
                switch (msg.PayloadCase)
                {
                    case RunnerControlMessage.PayloadOneofCase.Heartbeat:
                        // Parity with SignalR's RunnerHub.Heartbeat hub method:
                        // refresh the per-machine liveness timestamp so
                        // RunnerHeartbeatService doesn't flag the machine
                        // offline. Without this the API never sees the
                        // runner's gRPC pong as proof of life and would
                        // mark it Offline once SignalR is removed.
                        await using (var scope = scopeFactory.CreateAsyncScope())
                        {
                            var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
                            await db.RunnerMachines
                                .Where(m => m.Id == machineId)
                                .ExecuteUpdateAsync(s => s.SetProperty(
                                    m => m.LastHeartbeatAt, DateTimeOffset.UtcNow), ct);
                        }

                        await responseStream.WriteAsync(new ServerControlMessage
                        {
                            Heartbeat = new Heartbeat
                            {
                                SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                            },
                        }, ct);
                        break;

                    case RunnerControlMessage.PayloadOneofCase.ClisReport:
                        // Parity with SignalR's RunnerHub.ReportInstalledClis.
                        // Without this, removing SignalR would silently lose CLI
                        // / model discovery for any machine — agent-template UIs
                        // would stop seeing the runner's claude / codex / etc.
                        // Map proto → Mintokei.Runner.Contracts.Messages.InstalledCli
                        // (the canonical in-process type used by both transports).
                        var installed = msg.ClisReport.Clis
                            .Select(p => new Mintokei.Runner.Contracts.Messages.InstalledCli(
                                p.AgentToolKey,
                                p.Version,
                                p.Models.Count == 0
                                    ? null
                                    : p.Models.Select(pm => new Mintokei.Runner.Contracts.Messages.InstalledCliModel(
                                            pm.ModelId,
                                            string.IsNullOrEmpty(pm.DisplayName) ? null : pm.DisplayName,
                                            pm.IsDefault))
                                        .ToList()))
                            .ToList();
                        try
                        {
                            await runnerHost.OnInstalledClisReportedAsync(machineId, installed);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex,
                                "Error persisting InstalledClisReport from runner {MachineId}",
                                machineId);
                        }
                        break;

                    case RunnerControlMessage.PayloadOneofCase.Handshake:
                        logger.LogWarning(
                            "Unexpected Handshake from {MachineId} after initial handshake — ignored",
                            machineId);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal disconnect.
        }
        finally
        {
            controlChannelRegistry.Unregister(machineId, responseStream);
            await TeardownControlPresenceAsync(machineId, controlConnectionId);
            logger.LogInformation("Runner {MachineId} disconnected from gRPC Control stream", machineId);
        }
    }

    /// <summary>
    /// Disconnect teardown for the runner's presence. Deregisters the runner (connection-scoped, so a
    /// stale close can't evict a stream that already reconnected) and — only if this stream is still the
    /// machine's current presence connection — unregisters its file-server port, marks its remote process
    /// handles disconnected (transport gone, NOT exited, so they stay resumable), runs the product's
    /// disconnect reaction, and stamps <c>DisconnectedAt</c>.
    /// </summary>
    private async Task TeardownControlPresenceAsync(Guid machineId, string controlConnectionId)
    {
        // Race guard, the gRPC analog of SignalR's `machineId.HasValue` from GetMachineId: a reconnect
        // registers a new per-stream id and evicts ours from the tracker, so GetMachineId(ours) == null
        // means a newer stream already replaced us — deregister then no-ops and we skip the machine-wide
        // teardown (which would otherwise disconnect the live reconnected runner's processes).
        var stillCurrent = runnerRegistry.GetMachineId(controlConnectionId) is not null;
        runnerRegistry.DisconnectRunnerByConnection(controlConnectionId);
        if (!stillCurrent)
            return;

        fileServerPortStore.Unregister(machineId);
        remoteProcessStore.SetAllDisconnectedForMachine(machineId);

        try
        {
            await runnerHost.OnRunnerDisconnectedAsync(machineId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "gRPC Control disconnect teardown failed for machine {MachineId}", machineId);
        }

        // Stamp DisconnectedAt (best-effort). The call CT is already cancelled in the finally, so use
        // CancellationToken.None — the write is tiny and must complete regardless of the torn-down call.
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
            await db.RunnerMachines
                .Where(m => m.Id == machineId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DisconnectedAt, DateTimeOffset.UtcNow), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "gRPC Control disconnect failed to stamp DisconnectedAt for machine {MachineId}", machineId);
        }
    }

    /// <summary>
    /// File-watcher channel — separate physical stream so watcher events
    /// can never be head-of-line blocked by bulk file-read traffic on the
    /// query/bulk channels. Skeleton: server logs incoming change events
    /// but does NOT yet dispatch them to the SignalR-side subscriber bus,
    /// and does not send any StartFileWatcher / StopFileWatcher commands.
    /// Wiring those flips the watcher transport over; out of scope for
    /// this commit.
    /// </summary>
    public override async Task OpenWatcher(
        IAsyncStreamReader<WatcherClientMessage> requestStream,
        IServerStreamWriter<WatcherServerMessage> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;

        var machineIdClaim = context.GetHttpContext().User.FindFirst("machine_id")?.Value;
        if (machineIdClaim is null || !Guid.TryParse(machineIdClaim, out var machineId))
        {
            context.Status = new Status(StatusCode.Unauthenticated, "Machine identity not found in token");
            return;
        }

        watcherChannelRegistry.Register(machineId, responseStream);
        logger.LogInformation("Runner {MachineId} opened gRPC Watcher stream", machineId);

        // Re-issue StartFileWatcher to any active subscribers' workspaces /
        // tasks. We do this here rather than from Control's handshake because
        // the handshake fires before the runner has even read the handshake
        // response — at which point the OpenWatcher channel registry is
        // empty and the sends would silently no-op. Once we drop SignalR's
        // StartFileWatcher fallback, this is the only path that successfully
        // restores remote watchers on reconnect.
        try
        {
            await runnerHost.OnWatcherChannelOpenedAsync(machineId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "gRPC OpenWatcher restart-watchers failed for machine {MachineId}", machineId);
        }

        try
        {
            await foreach (var msg in requestStream.ReadAllAsync(ct))
            {
                if (msg.PayloadCase == WatcherClientMessage.PayloadOneofCase.Changed
                    && Guid.TryParse(msg.Changed.WorkspaceId, out var id))
                {
                    await runnerHost.OnRemoteFileSystemChangedAsync(id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal disconnect.
        }
        finally
        {
            watcherChannelRegistry.Unregister(machineId, responseStream);
            logger.LogInformation("Runner {MachineId} closed gRPC Watcher stream", machineId);
        }
    }

    /// <summary>
    /// Per-task channel — one stream per active process correlation. Persists
    /// per-correlation cumulative ack state into <see cref="RunnerOutboxChannel"/>
    /// rows so the API can resume from the right point on reconnect, but does
    /// NOT yet dispatch process output / completion to the SignalR-side
    /// processing pipeline. That dispatch lands in the next commit (the
    /// command-flow wiring), at which point the gRPC transport replaces
    /// SignalR for per-task message flow.
    /// </summary>
    public override async Task OpenTask(
        IAsyncStreamReader<TaskClientMessage> requestStream,
        IServerStreamWriter<TaskServerMessage> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;

        var machineIdClaim = context.GetHttpContext().User.FindFirst("machine_id")?.Value;
        if (machineIdClaim is null || !Guid.TryParse(machineIdClaim, out var machineId))
        {
            context.Status = new Status(StatusCode.Unauthenticated, "Machine identity not found in token");
            return;
        }

        // First message must be TaskOpen.
        if (!await requestStream.MoveNext(ct))
            return;

        var first = requestStream.Current;
        if (first.PayloadCase != TaskClientMessage.PayloadOneofCase.Open)
        {
            await responseStream.WriteAsync(new TaskServerMessage
            {
                OpenAck = new TaskOpenAck
                {
                    Success = false,
                    Error = "First message on Task stream must be TaskOpen",
                },
            }, ct);
            return;
        }

        if (!Guid.TryParse(first.Open.TaskCorrelationId, out var correlationId))
        {
            await responseStream.WriteAsync(new TaskServerMessage
            {
                OpenAck = new TaskOpenAck
                {
                    Success = false,
                    Error = "TaskOpen.task_correlation_id must be a Guid",
                },
            }, ct);
            return;
        }

        // Lookup or lazily create the per-correlation channel row. Returns
        // the runner-side seq high-water mark so the runner can resume from
        // the right point on reconnect (skip messages we've already seen).
        //
        // Concurrency note: the runner's EnsureOpenAsync is fire-and-forget
        // from three call sites (handshake re-open, OpenTaskRequest bootstrap,
        // HandleStartProcess) and isn't atomic with its in-process dedup
        // dictionary, so two OpenTask calls for the same (machine, correlation)
        // can land here at the same time. Both find no row, both Add, and the
        // second SaveChanges trips the (RunnerMachineId, CorrelationId) UNIQUE
        // index — leaving its stream unregistered while the runner thinks it
        // opened successfully. Catch the conflict and re-query so the loser
        // of the race still completes the handshake against the row the winner
        // committed.
        Guid channelId;
        long lastReceivedRunnerSeq;
        var runnerLastAcked = first.Open.LastAckedServerSeq;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
            RunnerOutboxChannel? channel = null;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                channel = await db.RunnerOutboxChannels
                    .FirstOrDefaultAsync(c => c.RunnerMachineId == machineId
                                           && c.CorrelationId == correlationId, ct);

                if (channel is null)
                {
                    channel = new RunnerOutboxChannel
                    {
                        Id = Guid.NewGuid(),
                        RunnerMachineId = machineId,
                        CorrelationId = correlationId,
                        LastAckedOutboundSequence = 0,
                        LastReceivedInboundSequence = 0,
                        CreatedAt = DateTimeOffset.UtcNow,
                    };
                    db.RunnerOutboxChannels.Add(channel);
                }

                // Runner is canonical for what it has acked from the server side.
                // If it reports a higher cumulative ack than we have on file
                // (e.g. our prior ack write was lost during a crash), advance.
                if (runnerLastAcked > channel.LastAckedOutboundSequence)
                    channel.LastAckedOutboundSequence = runnerLastAcked;

                try
                {
                    await db.SaveChangesAsync(ct);
                    break;
                }
                catch (DbUpdateException ex) when (attempt == 0 && IsUniqueChannelConflict(ex))
                {
                    // Concurrent OpenTask raced us in. Detach our pending insert
                    // and retry — the second pass will find the winner's row.
                    db.Entry(channel).State = EntityState.Detached;
                    logger.LogDebug(
                        "OpenTask raced on RunnerOutboxChannels insert for {MachineId}/{Correlation}, retrying",
                        machineId, correlationId);
                }
            }

            if (channel is null)
            {
                // Defensive — both attempts failed. Surface as handshake failure
                // so the runner sees an OpenAck error instead of a hang.
                await responseStream.WriteAsync(new TaskServerMessage
                {
                    OpenAck = new TaskOpenAck
                    {
                        Success = false,
                        Error = "Failed to acquire RunnerOutboxChannel row",
                    },
                }, ct);
                return;
            }

            channelId = channel.Id;
            lastReceivedRunnerSeq = channel.LastReceivedInboundSequence;
        }

        await responseStream.WriteAsync(new TaskServerMessage
        {
            OpenAck = new TaskOpenAck
            {
                Success = true,
                LastReceivedRunnerSeq = lastReceivedRunnerSeq,
            },
        }, ct);

        // Make the writer reachable from the rest of the API via the registry.
        taskChannelRegistry.Register(machineId, correlationId, responseStream);

        // Wake the outbox processor so any per-task message queued while the
        // stream was still being opened (e.g. the StartProcess that triggered
        // this open via OpenTaskRequest in the first place) gets drained
        // immediately instead of waiting for the next signal.
        outboxProcessor.NotifyMachineConnected(machineId);

        logger.LogInformation(
            "Runner {MachineId} opened gRPC Task stream for correlation {CorrelationId} (resume from runnerSeq>{Seq})",
            machineId, correlationId, lastReceivedRunnerSeq);

        try
        {
            while (await requestStream.MoveNext(ct))
            {
                var msg = requestStream.Current;
                switch (msg.PayloadCase)
                {
                    case TaskClientMessage.PayloadOneofCase.Ack:
                        await UpdateAckedOutboundAsync(channelId, msg.Ack.CumulativeSeq, ct);
                        await FlipOutboxMessagesAcknowledgedAsync(
                            machineId, correlationId, msg.Ack.CumulativeSeq, ct);
                        break;

                    case TaskClientMessage.PayloadOneofCase.Output:
                        // Per-machine dedup is the canonical gate so that the
                        // SignalR fallback path sees the same recorded state.
                        // Without this, after an API restart the handshake
                        // would report a stale RunnerMachine.LastReceivedInboundSequence
                        // and the runner would over-replay; SignalR's dedup
                        // table wouldn't recognize gRPC-delivered seqs and the
                        // agent pipeline would parse the same stream-json twice
                        // (visible as one assistant message rendered with tools
                        // between text parts and a second copy without).
                        if (await processOutputDispatcher.TryRecordInboundAsync(
                                machineId, msg.Output.Sequence,
                                InboundMessageType.ProcessOutputChunk, msg.Output.PayloadJson))
                        {
                            // Don't propagate the call CT into post-record work:
                            // the runner half-closes + Disposes the gRPC call
                            // immediately after writing the *terminal*
                            // ProcessCompleted, which trips this CT and skips
                            // the dispatch — the wrapper then hangs to its
                            // 60s/120s timeout. The row is already in
                            // InboundRunnerMessages at this point, so we must
                            // finish the in-process delivery regardless of
                            // whether the runner stays connected. Same logic
                            // applies to Output for consistency.
                            await TryAdvanceReceivedInboundAsync(channelId, msg.Output.Sequence, CancellationToken.None);
                            await processOutputDispatcher.DispatchProcessOutputAsync(machineId, msg.Output.PayloadJson);
                        }
                        break;

                    case TaskClientMessage.PayloadOneofCase.Completed:
                        if (await processOutputDispatcher.TryRecordInboundAsync(
                                machineId, msg.Completed.Sequence,
                                InboundMessageType.ProcessCompleted, msg.Completed.PayloadJson))
                        {
                            // See note on Output: must not honor `ct` here —
                            // the runner Disposes its OpenTask call right after
                            // writing this terminal Completed message, which
                            // cancels `ct` and (before this fix) silently
                            // skipped DispatchProcessCompletedAsync. That's
                            // exactly how Claude/Codex one-shots that produced
                            // output and exited successfully still reached
                            // their 60s/120s wrapper timeout.
                            await TryAdvanceReceivedInboundAsync(channelId, msg.Completed.Sequence, CancellationToken.None);
                            await processOutputDispatcher.DispatchProcessCompletedAsync(machineId, msg.Completed.PayloadJson);
                        }
                        break;

                    case TaskClientMessage.PayloadOneofCase.Wakeup:
                        logger.LogDebug(
                            "Task {CorrelationId} reported ScheduleWakeup via gRPC (skeleton — no dispatch)",
                            correlationId);
                        break;

                    case TaskClientMessage.PayloadOneofCase.Open:
                        logger.LogWarning(
                            "Task {CorrelationId} sent unexpected TaskOpen mid-stream — ignored",
                            correlationId);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal disconnect.
        }
        finally
        {
            taskChannelRegistry.Unregister(machineId, correlationId, responseStream);
            logger.LogInformation(
                "Runner {MachineId} closed gRPC Task stream for correlation {CorrelationId}",
                machineId, correlationId);
        }
    }

    /// <summary>
    /// Cumulative ack: bump <c>LastAckedOutboundSequence</c> only if the
    /// incoming value is greater. Uses ExecuteUpdateAsync so the row doesn't
    /// need to be tracked across the long-lived stream's lifetime.
    /// </summary>
    private async Task UpdateAckedOutboundAsync(Guid channelId, long cumulativeSeq, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
        await db.RunnerOutboxChannels
            .Where(c => c.Id == channelId && c.LastAckedOutboundSequence < cumulativeSeq)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastAckedOutboundSequence, cumulativeSeq), ct);
    }

    /// <summary>
    /// Flips per-machine <see cref="OutboxMessage.Status"/> to Acknowledged
    /// for any messages of this correlation up to <paramref name="cumulativeSeq"/>.
    /// This used to be done by the SignalR <c>RunnerHub.Acknowledge</c> hub
    /// method against <see cref="RunnerMachine.LastAckedOutboundSequence"/>;
    /// PR #6 drops the SignalR ack and routes the flip through this
    /// per-correlation gRPC ack handler instead.
    ///
    /// Without this, <see cref="OutboxCleanupService"/> never sees acked
    /// messages until the runner reconnects (the handshake is the only
    /// other path that flips Status), and <see cref="OutboxProcessorService"/>
    /// re-sends Sent-but-not-Acknowledged messages on every drain sweep
    /// — wasted bandwidth that the runner deduplicates but the API doesn't
    /// stop emitting.
    /// </summary>
    private async Task FlipOutboxMessagesAcknowledgedAsync(
        Guid machineId, Guid correlationId, long cumulativeSeq, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
        await db.OutboxMessages
            .Where(m => m.RunnerMachineId == machineId
                && m.CorrelationId == correlationId
                && m.SequenceNumber <= cumulativeSeq
                && m.Status != OutboxMessageStatus.Acknowledged)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxMessageStatus.Acknowledged)
                .SetProperty(m => m.AckedAt, DateTimeOffset.UtcNow), ct);
    }

    /// <summary>
    /// Monotonic seq advance from the runner side. Returns true when the
    /// row was actually updated (sequence is newly observed) — the caller
    /// uses this for dedup so that a runner reconnect / replay doesn't
    /// dispatch the same ProcessOutput line twice into RemoteProcessHandle.
    /// (SignalR's ReportProcessOutput dedups via the InboundRunnerMessages
    /// table; the gRPC path uses the per-correlation cursor instead.)
    /// </summary>
    private async Task<bool> TryAdvanceReceivedInboundAsync(Guid channelId, long sequence, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
        var rowsUpdated = await db.RunnerOutboxChannels
            .Where(c => c.Id == channelId && c.LastReceivedInboundSequence < sequence)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastReceivedInboundSequence, sequence), ct);
        return rowsUpdated > 0;
    }

    /// <summary>
    /// Small/medium FS RPC channel — request/response correlated by query_id.
    /// Skeleton: server logs incoming responses but does NOT yet route them
    /// to PendingQueryStore subscribers, and does not send any QueryServerMessage
    /// requests. The SignalR-based BrowseFilesystem / GetDirectoryTree / etc.
    /// flow remains the live path until cutover.
    /// </summary>
    public override async Task OpenQuery(
        IAsyncStreamReader<QueryClientMessage> requestStream,
        IServerStreamWriter<QueryServerMessage> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;

        var machineIdClaim = context.GetHttpContext().User.FindFirst("machine_id")?.Value;
        if (machineIdClaim is null || !Guid.TryParse(machineIdClaim, out var machineId))
        {
            context.Status = new Status(StatusCode.Unauthenticated, "Machine identity not found in token");
            return;
        }

        queryChannelRegistry.Register(machineId, responseStream);
        logger.LogInformation("Runner {MachineId} opened gRPC Query stream", machineId);

        try
        {
            await foreach (var msg in requestStream.ReadAllAsync(ct))
            {
                // Every QueryClientMessage oneof variant carries a result_json
                // string (or QueryError). Dispatch into PendingQueryStore the
                // same way the SignalR ReportQueryResult hub method does — the
                // caller's awaiting TaskCompletionSource doesn't care which
                // transport the JSON arrived on.
                var resultJson = msg.PayloadCase switch
                {
                    QueryClientMessage.PayloadOneofCase.Browse        => msg.Browse.ResultJson,
                    QueryClientMessage.PayloadOneofCase.DiscoverGit   => msg.DiscoverGit.ResultJson,
                    QueryClientMessage.PayloadOneofCase.RunCommand    => msg.RunCommand.ResultJson,
                    QueryClientMessage.PayloadOneofCase.DirectoryTree => msg.DirectoryTree.ResultJson,
                    QueryClientMessage.PayloadOneofCase.FileSize      => System.Text.Json.JsonSerializer.Serialize(
                                                                            new { sizeBytes = msg.FileSize.SizeBytes, exists = msg.FileSize.Exists }),
                    QueryClientMessage.PayloadOneofCase.PathOp        => msg.PathOp.ResultJson,
                    QueryClientMessage.PayloadOneofCase.FindFile      => msg.FindFile.ResultJson,
                    QueryClientMessage.PayloadOneofCase.Error         => null, // failure path below
                    _ => null,
                };
                if (resultJson is not null)
                {
                    pendingQueryStore.TryComplete(msg.QueryId, resultJson);
                }
                else if (msg.PayloadCase == QueryClientMessage.PayloadOneofCase.Error)
                {
                    logger.LogWarning("Query {QueryId} via gRPC returned error: {Message}",
                        msg.QueryId, msg.Error.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal disconnect.
        }
        finally
        {
            queryChannelRegistry.Unregister(machineId, responseStream);
            logger.LogInformation("Runner {MachineId} closed gRPC Query stream", machineId);
        }
    }

    /// <summary>
    /// Bulk read channel — file content / image reads. Server sends one
    /// BulkServerMessage per request (with a fresh query_id); runner replies
    /// with one or more BulkChunk messages terminated by <c>last = true</c>,
    /// or a single BulkError. Chunks from concurrent requests may interleave
    /// on this stream — reassembled here per query_id and dispatched into
    /// PendingQueryStore as the existing JSON-encoded response payload.
    ///
    /// Today GetFileContent / GetImageFile responses fit in a single chunk
    /// (the runner sends the full JSON response as one chunk with
    /// <c>last = true</c>). The reassembly buffer below already handles
    /// multi-chunk responses for the future — once large reads start being
    /// streamed in 256 KB slices the receiver code below doesn't change.
    /// </summary>
    public override async Task OpenBulk(
        IAsyncStreamReader<BulkClientMessage> requestStream,
        IServerStreamWriter<BulkServerMessage> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;

        var machineIdClaim = context.GetHttpContext().User.FindFirst("machine_id")?.Value;
        if (machineIdClaim is null || !Guid.TryParse(machineIdClaim, out var machineId))
        {
            context.Status = new Status(StatusCode.Unauthenticated, "Machine identity not found in token");
            return;
        }

        bulkChannelRegistry.Register(machineId, responseStream);
        logger.LogInformation("Runner {MachineId} opened gRPC Bulk stream", machineId);

        // query_id → ordered chunk byte arrays accumulated until last=true.
        var pending = new Dictionary<string, List<byte[]>>();

        try
        {
            await foreach (var msg in requestStream.ReadAllAsync(ct))
            {
                switch (msg.PayloadCase)
                {
                    case BulkClientMessage.PayloadOneofCase.Chunk:
                        if (!pending.TryGetValue(msg.QueryId, out var buf))
                        {
                            buf = new List<byte[]>();
                            pending[msg.QueryId] = buf;
                        }
                        buf.Add(msg.Chunk.Data.ToByteArray());

                        if (msg.Chunk.Last)
                        {
                            pending.Remove(msg.QueryId);
                            var totalLen = buf.Sum(b => b.Length);
                            var combined = new byte[totalLen];
                            var offset = 0;
                            foreach (var slice in buf)
                            {
                                Buffer.BlockCopy(slice, 0, combined, offset, slice.Length);
                                offset += slice.Length;
                            }
                            // The runner sends the existing JSON-encoded
                            // GetFileContentResponse / GetImageFileResponse
                            // as the chunk payload. Forward as-is; the API
                            // caller's WaitAsync deserializes.
                            var resultJson = System.Text.Encoding.UTF8.GetString(combined);
                            pendingQueryStore.TryComplete(msg.QueryId, resultJson);
                        }
                        break;

                    case BulkClientMessage.PayloadOneofCase.Error:
                        pending.Remove(msg.QueryId);
                        logger.LogWarning("Bulk {QueryId} via gRPC returned error: {Message}",
                            msg.QueryId, msg.Error.Message);
                        // Complete the pending TCS with an empty payload so the
                        // caller's WaitAsync surfaces a deserialization failure
                        // rather than hanging until timeout. (Future: add an
                        // explicit error path through PendingQueryStore.)
                        pendingQueryStore.TryComplete(msg.QueryId, "{}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal disconnect.
        }
        finally
        {
            bulkChannelRegistry.Unregister(machineId, responseStream);
            logger.LogInformation("Runner {MachineId} closed gRPC Bulk stream", machineId);
        }
    }

    /// <summary>
    /// Detects the SQLite unique-index violation raised by a concurrent insert
    /// against <c>RunnerOutboxChannels (RunnerMachineId, CorrelationId)</c>.
    /// EF surfaces it as <see cref="DbUpdateException"/>; the inner provider
    /// exception carries SQLite error code 19 (constraint) with the index
    /// name in its message. We match on the index columns so a different
    /// unique-constraint failure on the same table doesn't get swallowed.
    /// </summary>
    private static bool IsUniqueChannelConflict(DbUpdateException ex)
    {
        for (var inner = (Exception?)ex; inner is not null; inner = inner.InnerException)
        {
            var msg = inner.Message;
            if (msg.Contains("UNIQUE constraint failed", StringComparison.Ordinal)
                && msg.Contains("RunnerOutboxChannels.RunnerMachineId", StringComparison.Ordinal)
                && msg.Contains("RunnerOutboxChannels.CorrelationId", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
