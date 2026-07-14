using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner;

/// <summary>
/// gRPC client skeleton — runs alongside the SignalR-based RunnerHostedService
/// when <c>Runner:EnableGrpc</c> is true. Connects to the API's Control RPC,
/// performs the handshake, then sends periodic heartbeats and reads server
/// responses. Reconnects with exponential backoff on transport errors.
///
/// This service is intentionally non-routing: it does not consume the local
/// outbox, drive process execution, or replace SignalR. Its only purpose is
/// to exercise the gRPC transport end-to-end while the message flow is wired
/// in subsequent steps.
/// </summary>
public sealed class GrpcRunnerHostedService(
    IOptions<RunnerOptions> options,
    TokenRefreshService tokenRefreshService,
    GrpcTaskStreamManager taskStreamManager,
    GrpcWatcherStreamManager watcherStreamManager,
    GrpcQueryStreamManager queryStreamManager,
    GrpcBulkStreamManager bulkStreamManager,
    FileWatcherService fileWatcherService,
    LocalOutbox outbox,
    RunnerFileServer fileServer,
    IConfiguration configuration,
    ILogger<GrpcRunnerHostedService> logger) : BackgroundService
{
    /// <summary>
    /// Delegate set by <see cref="RunnerHostedService"/> at startup so that
    /// inbound <c>QueryServerMessage</c>s on the OpenQuery stream can be
    /// dispatched through the same handlers the SignalR client methods use.
    /// </summary>
    public Func<QueryServerMessage, Task>? QueryDispatcher { get; set; }

    /// <summary>Same role as <see cref="QueryDispatcher"/> but for OpenBulk.</summary>
    public Func<BulkServerMessage, Task>? BulkDispatcher { get; set; }

    /// <summary>
    /// Probes the given CLI specs and returns the installed set. Set by <c>RunnerHostedService</c> ONLY
    /// when the runner is running without SignalR — CLI discovery then rides the gRPC Control handshake
    /// (its <c>cli_probes</c>) and the result is reported back as an <c>InstalledClisReport</c> on the
    /// Control stream. Null when SignalR owns CLI discovery (default), in which case any <c>cli_probes</c>
    /// on the handshake are ignored so there's no double report.
    /// </summary>
    public Func<IReadOnlyList<Mintokei.Runner.Contracts.Messages.CliProbeSpec>, CancellationToken,
        Task<IReadOnlyList<Mintokei.Runner.Contracts.Messages.InstalledCli>>>? CliProber { get; set; }

    /// <summary>
    /// Returns the correlation IDs of processes currently tracked by
    /// RunnerHostedService. Set by RunnerHostedService at startup so the
    /// gRPC handshake can report active processes for server-side
    /// reconciliation (mirrors what SignalR's PerformHandshakeAsync does
    /// via <c>_handles.Keys</c>).
    /// </summary>
    public Func<IReadOnlyCollection<Guid>>? ActiveCorrelationIdsProvider { get; set; }
    private static readonly TimeSpan HeartbeatInterval     = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReconnectInitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectMaxDelay     = TimeSpan.FromSeconds(30);

    private readonly RunnerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Runner:EnableGrpc", defaultValue: true))
        {
            logger.LogInformation("Runner:EnableGrpc is false — gRPC client disabled");
            return;
        }

        if (_options.MachineId is null)
        {
            logger.LogWarning("Runner has no MachineId — cannot start gRPC Control stream");
            return;
        }

        var grpcUrlForLog = !string.IsNullOrEmpty(_options.GrpcBackendUrl)
            ? _options.GrpcBackendUrl
            : _options.BackendUrl;
        logger.LogInformation("GrpcRunnerHostedService starting (gRPC={GrpcUrl})", grpcUrlForLog);

        var backoff = ReconnectInitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunControlStreamAsync(stoppingToken);
                backoff = ReconnectInitialDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "gRPC Control stream errored, reconnecting in {Delay:N0}s",
                    backoff.TotalSeconds);
            }

            try { await Task.Delay(backoff, stoppingToken); }
            catch (OperationCanceledException) { break; }

            backoff = TimeSpan.FromSeconds(
                Math.Min(ReconnectMaxDelay.TotalSeconds, backoff.TotalSeconds * 2));
        }
    }

    private async Task RunControlStreamAsync(CancellationToken ct)
    {
        // Prefer an explicit gRPC URL (required in local dev where Kestrel
        // can't host h2c and HTTP/1.1 on the same plain-HTTP listener).
        // Falls back to BackendUrl in production where an ingress path-routes
        // /mintokei.runner.v1.RunnerLink/* to the dedicated h2c port.
        var grpcUrl = !string.IsNullOrEmpty(_options.GrpcBackendUrl)
            ? _options.GrpcBackendUrl
            : _options.BackendUrl;
        using var channel = GrpcChannel.ForAddress(grpcUrl);
        var client = new RunnerLink.RunnerLinkClient(channel);

        var token = await tokenRefreshService.GetCurrentTokenAsync();
        var headers = new Metadata();
        if (!string.IsNullOrEmpty(token))
            headers.Add("Authorization", $"Bearer {token}");

        using var controlCall = client.Control(headers, cancellationToken: ct);

        // Serializes writes to the Control request stream (gRPC client streams disallow concurrent
        // writes). Both the heartbeat loop and the one-shot InstalledClisReport go through it.
        using var controlWriteLock = new SemaphoreSlim(1, 1);

        // Build handshake request with the same fields SignalR's PerformHandshakeAsync
        // sends. Without these the server-side handshake handler can't:
        //   - mark OutboxMessages up to LastAckedOutboundSequence as Acknowledged
        //   - reconcile active correlations (would mark all in-progress tasks
        //     as Cancelled because the runner appears to have nothing running)
        //   - register the runner's file-server port for tunnel previews
        var handshakeRequest = new HandshakeRequest
        {
            FileServerPort = fileServer.Port,
            LastAckedOutboundSequence = await outbox.GetLastAckedBackendSequenceAsync(),
        };
        var activeCorrelations = ActiveCorrelationIdsProvider?.Invoke();
        if (activeCorrelations is { Count: > 0 })
        {
            foreach (var id in activeCorrelations)
                handshakeRequest.ActiveCorrelationIds.Add(id.ToString());
        }

        await controlCall.RequestStream.WriteAsync(new RunnerControlMessage
        {
            Handshake = handshakeRequest,
        });

        if (!await controlCall.ResponseStream.MoveNext(ct))
            throw new IOException("Server closed Control stream before handshake response");

        var first = controlCall.ResponseStream.Current;
        if (first.PayloadCase != ServerControlMessage.PayloadOneofCase.Handshake)
            throw new IOException($"Expected Handshake response, got {first.PayloadCase}");
        if (!first.Handshake.Success)
            throw new IOException($"Handshake rejected: {first.Handshake.Error}");

        logger.LogInformation(
            "gRPC Control handshake successful (machineId={MachineId})",
            first.Handshake.MachineId);

        // Running without SignalR: CLI discovery rides this stream. The server delivered the probe specs
        // in the handshake response; probe them and report back over Control. Fire-and-forget so the ~5s
        // probing doesn't delay opening the data streams; the write is serialized via controlWriteLock.
        // (When SignalR is enabled, CliProber is null and the SignalR handshake owns CLI discovery — the
        // cli_probes here are simply ignored, so there's no double report.)
        if (CliProber is { } prober && first.Handshake.CliProbes.Count > 0)
        {
            _ = ReportInstalledClisAsync(prober, controlCall.RequestStream, controlWriteLock,
                first.Handshake.CliProbes, ct);
        }

        // Register the live client with the per-task stream manager so that
        // any caller that wants to open an OpenTask stream for a process
        // correlation has access to it. Unregistered in the finally below
        // so manager.IsOpen and EnsureOpenAsync correctly return false
        // while disconnected.
        taskStreamManager.RegisterClient(client, headers);

        // Open the secondary streams over the same HTTP/2 channel — they
        // multiplex as separate streams, so a flood of bulk traffic on one
        // stream cannot head-of-line block another. Per-task OpenTask streams
        // are opened on demand once the command flow is wired.
        using var watcherCall = client.OpenWatcher(headers, cancellationToken: ct);
        watcherStreamManager.RegisterWriter(watcherCall.RequestStream);
        logger.LogInformation("gRPC Watcher stream opened");
        using var queryCall = client.OpenQuery(headers, cancellationToken: ct);
        queryStreamManager.RegisterWriter(queryCall.RequestStream);
        logger.LogInformation("gRPC Query stream opened");
        using var bulkCall = client.OpenBulk(headers, cancellationToken: ct);
        bulkStreamManager.RegisterWriter(bulkCall.RequestStream);
        logger.LogInformation("gRPC Bulk stream opened");

        // Eagerly re-open OpenTask streams for any process correlations that
        // were already running before the (re)connect. Without this, a runner
        // that reconnects with pending ProcessOutput / ProcessCompleted in its
        // local outbox would have nowhere to drain it: the API only fires
        // OpenTaskRequest when *its* outbox has a per-task command for that
        // correlation, so a pure-uplink-pending runner would sit blocked.
        //
        // The SignalR fallback used to mask this — pending outbox would just
        // re-flow over the SignalR ReportProcessOutput hub method. With that
        // fallback gone (PR 5+) the runner has to take responsibility for
        // its own per-task plumbing on reconnect.
        if (activeCorrelations is { Count: > 0 })
        {
            foreach (var correlationId in activeCorrelations)
            {
                // Fire-and-forget — opening involves a TaskOpen/TaskOpenAck
                // round-trip that we don't want to serialize handshake on.
                _ = taskStreamManager.EnsureOpenAsync(correlationId);
            }
            logger.LogInformation(
                "Reopened {Count} OpenTask stream(s) for active correlations after handshake",
                activeCorrelations.Count);
        }

        // Run all stream pumps concurrently. First failure cancels the others
        // and bubbles up to the reconnect loop.
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sendTask    = SendHeartbeatLoopAsync(controlCall.RequestStream, controlWriteLock, streamCts.Token);
        var readTask    = ReadResponseLoopAsync(controlCall.ResponseStream, streamCts.Token);
        var watcherTask = ReadWatcherCommandsAsync(watcherCall.ResponseStream, streamCts.Token);
        var queryTask   = ReadQueryCommandsAsync(queryCall.ResponseStream, streamCts.Token);
        var bulkTask    = ReadBulkCommandsAsync(bulkCall.ResponseStream, streamCts.Token);

        try
        {
            var completed = await Task.WhenAny(sendTask, readTask, watcherTask, queryTask, bulkTask);
            streamCts.Cancel();

            try { await controlCall.RequestStream.CompleteAsync(); } catch { /* best effort */ }
            try { await watcherCall.RequestStream.CompleteAsync(); } catch { /* best effort */ }
            try { await queryCall.RequestStream.CompleteAsync(); } catch { /* best effort */ }
            try { await bulkCall.RequestStream.CompleteAsync(); } catch { /* best effort */ }

            await completed; // observe exception, if any
        }
        finally
        {
            // Tear down all per-task streams so callers immediately see
            // IsOpen == false and stop sending up the dead path. The next
            // handshake will re-register the client and per-task streams
            // can be re-opened on demand.
            taskStreamManager.UnregisterClient();
            watcherStreamManager.UnregisterWriter(watcherCall.RequestStream);
            queryStreamManager.UnregisterWriter(queryCall.RequestStream);
            bulkStreamManager.UnregisterWriter(bulkCall.RequestStream);
        }
    }

    private async Task SendHeartbeatLoopAsync(
        IClientStreamWriter<RunnerControlMessage> writer,
        SemaphoreSlim writeLock,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatInterval, ct); }
            catch (OperationCanceledException) { return; }

            var heartbeat = new RunnerControlMessage
            {
                Heartbeat = new Heartbeat
                {
                    SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            };
            await writeLock.WaitAsync(ct);
            try { await writer.WriteAsync(heartbeat); }
            finally { writeLock.Release(); }
        }
    }

    /// <summary>
    /// One-shot: probe the CLI specs the server delivered in the gRPC handshake and report the result as
    /// an <c>InstalledClisReport</c> on the Control stream. Only invoked when the runner runs without
    /// SignalR (<see cref="CliProber"/> set). Best-effort; a probe/report failure just logs.
    /// </summary>
    private async Task ReportInstalledClisAsync(
        Func<IReadOnlyList<Mintokei.Runner.Contracts.Messages.CliProbeSpec>, CancellationToken,
            Task<IReadOnlyList<Mintokei.Runner.Contracts.Messages.InstalledCli>>> prober,
        IClientStreamWriter<RunnerControlMessage> writer,
        SemaphoreSlim writeLock,
        IReadOnlyList<CliProbeRequest> probeRequests,
        CancellationToken ct)
    {
        try
        {
            var specs = probeRequests
                .Select(p => new Mintokei.Runner.Contracts.Messages.CliProbeSpec(
                    p.AgentToolKey, p.BinaryName, p.VersionArgs,
                    string.IsNullOrEmpty(p.VersionRegex) ? null : p.VersionRegex))
                .ToList();

            var installed = await prober(specs, ct);

            var report = new InstalledClisReport();
            foreach (var cli in installed)
            {
                var protoCli = new InstalledCli
                {
                    AgentToolKey = cli.AgentToolKey,
                    Version = cli.Version,
                };
                if (cli.Models is not null)
                {
                    foreach (var m in cli.Models)
                    {
                        protoCli.Models.Add(new InstalledCliModel
                        {
                            ModelId = m.ModelId,
                            DisplayName = m.DisplayName ?? string.Empty,
                            IsDefault = m.IsDefault,
                        });
                    }
                }
                report.Clis.Add(protoCli);
            }

            await writeLock.WaitAsync(ct);
            try { await writer.WriteAsync(new RunnerControlMessage { ClisReport = report }); }
            finally { writeLock.Release(); }

            logger.LogInformation("Reported {Count} installed CLI(s) to backend over gRPC Control", installed.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to probe/report installed CLIs over gRPC Control");
        }
    }

    private async Task ReadResponseLoopAsync(
        IAsyncStreamReader<ServerControlMessage> reader,
        CancellationToken ct)
    {
        while (await reader.MoveNext(ct))
        {
            var msg = reader.Current;
            switch (msg.PayloadCase)
            {
                case ServerControlMessage.PayloadOneofCase.Heartbeat:
                    logger.LogTrace("Heartbeat pong from server");
                    break;
                case ServerControlMessage.PayloadOneofCase.CliProbe:
                    // Skeleton: CLI probing still flows over SignalR. Will be
                    // wired here when the gRPC transport becomes the source
                    // of truth.
                    logger.LogDebug(
                        "Received CliProbe for {Tool} via gRPC (skipped)",
                        msg.CliProbe.AgentToolKey);
                    break;
                case ServerControlMessage.PayloadOneofCase.OpenTask:
                    // API is asking us to open an OpenTask stream for this
                    // correlation so it can deliver per-task commands. Without
                    // this signal we'd never know about new correlations
                    // (StartProcess used to bootstrap implicitly via SignalR
                    // ReceiveMessage; once that path is gone, this is the
                    // only way the runner learns about a new task).
                    if (Guid.TryParse(msg.OpenTask.TaskCorrelationId, out var bootstrapCorrelation))
                    {
                        // Fire-and-forget: opening involves a server round-trip
                        // (TaskOpen/TaskOpenAck) which we don't want to block
                        // the Control reader on.
                        _ = taskStreamManager.EnsureOpenAsync(bootstrapCorrelation);
                        logger.LogDebug(
                            "Received OpenTaskRequest for correlation {CorrelationId} via gRPC",
                            bootstrapCorrelation);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Received OpenTaskRequest with unparseable correlation '{Raw}' — ignored",
                            msg.OpenTask.TaskCorrelationId);
                    }
                    break;
            }
        }
    }

    private async Task ReadWatcherCommandsAsync(
        IAsyncStreamReader<WatcherServerMessage> reader,
        CancellationToken ct)
    {
        while (await reader.MoveNext(ct))
        {
            var msg = reader.Current;
            switch (msg.PayloadCase)
            {
                case WatcherServerMessage.PayloadOneofCase.Start:
                    fileWatcherService.Start(msg.Start.WorkspaceId, msg.Start.Path);
                    break;
                case WatcherServerMessage.PayloadOneofCase.Stop:
                    fileWatcherService.Stop(msg.Stop.WorkspaceId);
                    break;
            }
        }
    }

    private async Task ReadQueryCommandsAsync(
        IAsyncStreamReader<QueryServerMessage> reader,
        CancellationToken ct)
    {
        while (await reader.MoveNext(ct))
        {
            var msg = reader.Current;
            var dispatcher = QueryDispatcher;
            if (dispatcher is null)
            {
                logger.LogWarning(
                    "Received Query {QueryId} but no dispatcher registered — dropped",
                    msg.QueryId);
                continue;
            }

            try
            {
                await dispatcher(msg);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Query {QueryId} dispatch ({PayloadCase}) failed",
                    msg.QueryId, msg.PayloadCase);
            }
        }
    }

    private async Task ReadBulkCommandsAsync(
        IAsyncStreamReader<BulkServerMessage> reader,
        CancellationToken ct)
    {
        while (await reader.MoveNext(ct))
        {
            var msg = reader.Current;
            var dispatcher = BulkDispatcher;
            if (dispatcher is null)
            {
                logger.LogWarning(
                    "Received Bulk {QueryId} but no dispatcher registered — dropped",
                    msg.QueryId);
                continue;
            }

            try
            {
                await dispatcher(msg);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Bulk {QueryId} dispatch ({PayloadCase}) failed",
                    msg.QueryId, msg.PayloadCase);
            }
        }
    }
}
