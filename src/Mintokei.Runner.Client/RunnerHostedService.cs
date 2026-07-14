using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts;
using Mintokei.Runner.Contracts.Messages;
using GrpcContracts = Mintokei.Runner.Contracts.Grpc;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.Runner.Filesystem;

namespace Mintokei.Runner;

/// <summary>
/// Long-running hosted service that runs the runner's local outbox drain and the gRPC OpenTask command
/// dispatcher, and proxies process execution requests using the shared CommandLineRunner. The gRPC Control
/// connection itself (presence, liveness, CLI discovery) is owned by <see cref="GrpcRunnerHostedService"/>.
/// </summary>
public sealed class RunnerHostedService : BackgroundService
{
    private readonly RunnerOptions _options;
    private readonly ICommandLineRunner _commandLineRunner;
    private readonly LocalOutbox _outbox;
    private readonly TokenRefreshService _tokenRefreshService;
    private readonly FileWatcherService _fileWatcherService;
    private readonly GrpcTaskStreamManager _grpcTaskStreams;
    private readonly GrpcWatcherStreamManager _grpcWatcherStream;
    private readonly GrpcQueryStreamManager _grpcQueryStream;
    private readonly GrpcBulkStreamManager _grpcBulkStream;
    private readonly GrpcRunnerHostedService _grpcRunnerHostedService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RunnerHostedService> _logger;


    // Thread-safe state
    private long _lastAckedBackendSequence;

    // Process handles keyed by correlation ID
    private readonly ConcurrentDictionary<Guid, IProcessHandle> _handles = new();

    // Drain signal: bounded channel with DropOldest replaces AsyncAutoResetEvent (no memory leak)
    private readonly Channel<bool> _drainSignal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly RunnerFileServer _fileServer;

    public RunnerHostedService(
        IOptions<RunnerOptions> options,
        ICommandLineRunner commandLineRunner,
        LocalOutbox outbox,
        TokenRefreshService tokenRefreshService,
        FileWatcherService fileWatcherService,
        RunnerFileServer fileServer,
        GrpcTaskStreamManager grpcTaskStreams,
        GrpcWatcherStreamManager grpcWatcherStream,
        GrpcQueryStreamManager grpcQueryStream,
        GrpcBulkStreamManager grpcBulkStream,
        GrpcRunnerHostedService grpcRunnerHostedService,
        IServiceProvider serviceProvider,
        ILogger<RunnerHostedService> logger)
    {
        _options = options.Value;
        _commandLineRunner = commandLineRunner;
        _outbox = outbox;
        _tokenRefreshService = tokenRefreshService;
        _fileWatcherService = fileWatcherService;
        _fileServer = fileServer;
        _grpcTaskStreams = grpcTaskStreams;
        _grpcWatcherStream = grpcWatcherStream;
        _grpcQueryStream = grpcQueryStream;
        _grpcBulkStream = grpcBulkStream;
        _grpcRunnerHostedService = grpcRunnerHostedService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Interlocked.Exchange(ref _lastAckedBackendSequence, await _outbox.GetLastAckedBackendSequenceAsync());

        // Wire the gRPC OpenQuery / OpenBulk readers to this service's query / bulk handlers, hand the
        // gRPC handshake the live process correlations for reconnect reconciliation, and provide the CLI
        // prober so the gRPC Control handshake's probe specs get probed + reported over Control.
        _grpcRunnerHostedService.QueryDispatcher = DispatchGrpcQueryAsync;
        _grpcRunnerHostedService.BulkDispatcher = DispatchGrpcBulkAsync;
        _grpcRunnerHostedService.ActiveCorrelationIdsProvider = () => _handles.Keys.ToArray();
        _grpcRunnerHostedService.CliProber = ProbeInstalledClisAsync;

        // File-watcher change notifications are delivered over the gRPC OpenWatcher stream.
        _fileWatcherService.OnFileSystemChanged = OnFileSystemChangedAsync;

        _logger.LogInformation("Runner transport: gRPC only (presence + liveness + CLI + data plane).");

        // The Control connection itself is owned by GrpcRunnerHostedService (presence, liveness via the
        // self-heartbeat, CLI discovery). This service runs the local outbox drain and the gRPC OpenTask
        // command dispatcher; per-correlation ack/replay is handled on each OpenTask stream.
        await Task.WhenAll(
            DrainOutboxLoopAsync(stoppingToken),
            DispatchGrpcTaskCommandsLoopAsync(stoppingToken));
    }

    /// <summary>
    /// Probes each CLI spec (in parallel) and returns the installed set. Handed to
    /// <c>GrpcRunnerHostedService.CliProber</c> so CLI discovery rides the gRPC Control handshake (the
    /// server delivers the probe specs; the runner reports back an InstalledClisReport).
    /// </summary>
    private async Task<IReadOnlyList<InstalledCli>> ProbeInstalledClisAsync(
        IReadOnlyList<CliProbeSpec> probes, CancellationToken ct)
    {
        var results = await Task.WhenAll(probes.Select(p => ProbeSingleAsync(p, ct)));
        return results.OfType<InstalledCli>().ToList();
    }

    private async Task<InstalledCli?> ProbeSingleAsync(CliProbeSpec spec, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            // Resolve the full path first. On Windows, Process.Start with
            // UseShellExecute=false only auto-appends .exe — it does not walk
            // PATHEXT, so npm-installed CLIs that ship as .cmd shims (e.g.
            // codex, copilot) are invisible without explicit resolution.
            var resolved = await ResolveExecutablePathAsync(spec.BinaryName, cts.Token);
            if (resolved is null) return null;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = resolved,
                Arguments = spec.VersionArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0) return null;

            var stdout = (await stdoutTask).Trim();
            if (string.IsNullOrEmpty(stdout)) return null;

            string version;
            if (!string.IsNullOrEmpty(spec.VersionRegex)
                && System.Text.RegularExpressions.Regex.Match(stdout, spec.VersionRegex) is { Success: true } match)
            {
                version = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
            }
            else
            {
                version = VersionStringParser.Parse(stdout, spec.BinaryName);
            }

            // CLI is installed — also try to enumerate available models from
            // whatever account the user is logged in as on this machine. The
            // per-CLI providers shell out via the same ICommandLineRunner, so
            // anything they report is grounded in the actual local install.
            var models = await DiscoverModelsAsync(spec.AgentToolKey, ct);

            return new InstalledCli(spec.AgentToolKey, version, models);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Probe for {Binary} timed out", spec.BinaryName);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // Binary not on PATH — not installed
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probe for {Binary} failed", spec.BinaryName);
            return null;
        }
    }

    private async Task<List<InstalledCliModel>?> DiscoverModelsAsync(string agentToolKeyString, CancellationToken ct)
    {
        if (!Enum.TryParse<AgentToolKey>(agentToolKeyString, ignoreCase: false, out var key))
            return null;

        var provider = _serviceProvider.GetKeyedService<IModelDiscoveryProvider>(key);
        if (provider is null)
        {
            _logger.LogDebug("No model discovery provider registered for {Key}", key);
            return null;
        }

        try
        {
            var list = await provider.DiscoverModelsAsync(ct);
            if (list.Models.Count == 0)
                return null;

            return list.Models
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new InstalledCliModel(m.Id, m.DisplayName, m.IsDefault))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Model discovery failed for {Key}", key);
            return null;
        }
    }

    private static async Task<string?> ResolveExecutablePathAsync(string binaryName, CancellationToken ct)
    {
        var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows);
        var command = isWindows ? "where" : "which";

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = binaryName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0) return null;

            var output = (await stdoutTask).Trim();
            if (string.IsNullOrEmpty(output)) return null;

            var candidates = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            if (isWindows)
            {
                return candidates.FirstOrDefault(p =>
                        p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                    ?? candidates[0];
            }

            return candidates[0];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Background loop that drains pending outbox messages via the per-task
    /// gRPC OpenTask streams. Woken by drain signal when new messages are
    /// inserted, or by a periodic 5s fallback timer to retry messages whose
    /// per-task stream wasn't open at the previous attempt.
    ///
    /// When no OpenTask stream is open for a message's correlation,
    /// <see cref="TrySendUplinkViaGrpcAsync"/> returns false and we leave
    /// the message Pending. The next sweep retries; in steady state the
    /// stream is opened either by the runner itself (HandleStartProcess →
    /// EnsureOpenAsync, post-handshake reopen for active correlations) or
    /// by the API sending OpenTaskRequest via the Control stream.
    /// </summary>
    private async Task DrainOutboxLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for signal or periodic fallback (5s) to retry messages
                // whose per-task stream wasn't open at the previous attempt.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                try { await _drainSignal.Reader.ReadAsync(cts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* fallback timer */ }

                // Filter to correlations whose per-task gRPC stream is currently
                // open. Without this, dead pre-restart correlations sit at the
                // head of the queue and starve every live one — the drain pulls
                // the same 200 oldest Pending rows on every sweep, finds them
                // all closed, skips them all, never advances the cursor. See
                // commit history around `head-of-line blocking in outbox drain`.
                var openCorrelations = _grpcTaskStreams.OpenCorrelations();
                var pending = await _outbox.GetPendingAsync(200, openCorrelations);
                foreach (var (seq, type, payload, correlationId) in pending)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var sentViaGrpc = await TrySendUplinkViaGrpcAsync(seq, type, payload, correlationId, ct);
                        if (!sentViaGrpc)
                        {
                            // OpenTask stream isn't open for this correlation
                            // yet. Leave Pending and skip — per-correlation
                            // ordering is preserved automatically because
                            // subsequent messages for the same correlation
                            // will also see the closed stream and skip too.
                            // The next sweep (drain signal or 5s fallback)
                            // retries.
                            continue;
                        }
                        await _outbox.MarkSentAsync(seq);

                        // Close the per-task gRPC stream once the terminal
                        // ProcessCompleted has been delivered. Without this,
                        // streams accumulate per process and only get cleaned
                        // up on connection drop. Fire-and-forget — the close
                        // does not need to complete before draining the next
                        // message.
                        if (type == "ReportProcessCompleted" && correlationId is Guid corr)
                        {
                            _ = _grpcTaskStreams.CloseAsync(corr);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to drain outbox message seq {Seq}, will retry", seq);
                        break; // Stop draining — will retry next cycle
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in outbox drain loop");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    /// <summary>
    /// Dispatches an inbound <c>QueryServerMessage</c> from the OpenQuery
    /// stream to the same handlers the SignalR client methods invoke. The
    /// handlers reach for <see cref="RespondToQueryAsync"/> to send back
    /// — that helper prefers the gRPC OpenQuery uplink and falls back to
    /// SignalR <c>ReportQueryResult</c> when no stream is registered.
    /// </summary>
    private async Task DispatchGrpcQueryAsync(GrpcContracts.QueryServerMessage msg)
    {
        switch (msg.PayloadCase)
        {
            case GrpcContracts.QueryServerMessage.PayloadOneofCase.Browse:
                await OnBrowseFilesystemAsync(msg.QueryId, string.IsNullOrEmpty(msg.Browse.Path) ? null : msg.Browse.Path);
                break;
            case GrpcContracts.QueryServerMessage.PayloadOneofCase.DiscoverGit:
                await OnDiscoverGitRepositoriesAsync(msg.QueryId, msg.DiscoverGit.Path);
                break;
            case GrpcContracts.QueryServerMessage.PayloadOneofCase.RunCommand:
                await OnRunCommandAsync(
                    msg.QueryId,
                    msg.RunCommand.WorkingDirectory,
                    msg.RunCommand.Executable,
                    msg.RunCommand.Arguments,
                    msg.RunCommand.TimeoutMs);
                break;
            case GrpcContracts.QueryServerMessage.PayloadOneofCase.DirectoryTree:
                await OnGetDirectoryTreeAsync(
                    msg.QueryId,
                    msg.DirectoryTree.BasePath,
                    string.IsNullOrEmpty(msg.DirectoryTree.SubPath) ? null : msg.DirectoryTree.SubPath,
                    msg.DirectoryTree.MaxDepth,
                    msg.DirectoryTree.UseGitIgnore);
                break;
            case GrpcContracts.QueryServerMessage.PayloadOneofCase.FileSize:
                await OnGetFileSizeAsync(msg.QueryId, msg.FileSize.Path);
                break;
            case GrpcContracts.QueryServerMessage.PayloadOneofCase.PathOp:
                await OnPathOperationAsync(msg.QueryId, msg.PathOp.Operation, msg.PathOp.Args.ToArray());
                break;
            case GrpcContracts.QueryServerMessage.PayloadOneofCase.FindFile:
                await OnFindFileAsync(msg.QueryId, msg.FindFile.BasePath, msg.FindFile.Suffix, msg.FindFile.Limit);
                break;
            default:
                _logger.LogWarning("Unknown QueryServerMessage payload {Case} for query {QueryId}", msg.PayloadCase, msg.QueryId);
                break;
        }
    }

    /// <summary>
    /// Common send path for query responses via the gRPC OpenQuery uplink.
    /// All existing query handlers route through this so they don't need
    /// to know which physical stream delivered the request — the OpenQuery
    /// channel is the one and only response lane for small/medium queries.
    /// </summary>
    private async Task RespondToQueryAsync(string requestId, string resultJson)
    {
        if (await _grpcQueryStream.TrySendResultAsync(requestId, resultJson))
            return;

        // OpenQuery isn't currently registered (runner just reconnected, or
        // disconnected entirely). The API-side caller's pendingQueryStore
        // will time out and surface the failure to the requesting endpoint;
        // there's nothing useful we can do here other than log.
        _logger.LogWarning(
            "Failed to report query result for {RequestId} — OpenQuery stream not registered",
            requestId);
    }

    /// <summary>
    /// Dispatches an inbound <c>BulkServerMessage</c> from the OpenBulk
    /// stream to the same handlers the SignalR client methods invoke.
    /// </summary>
    private async Task DispatchGrpcBulkAsync(GrpcContracts.BulkServerMessage msg)
    {
        switch (msg.PayloadCase)
        {
            case GrpcContracts.BulkServerMessage.PayloadOneofCase.File:
                await OnGetFileContentAsync(msg.QueryId, msg.File.BasePath, msg.File.FilePath);
                break;
            case GrpcContracts.BulkServerMessage.PayloadOneofCase.Image:
                await OnGetImageFileAsync(msg.QueryId, msg.Image.BasePath, msg.Image.FilePath);
                break;
            default:
                _logger.LogWarning("Unknown BulkServerMessage payload {Case} for query {QueryId}", msg.PayloadCase, msg.QueryId);
                break;
        }
    }

    /// <summary>
    /// Common send path for bulk-payload responses (file content, images)
    /// via the gRPC OpenBulk uplink. Sent as a single chunk with last=true;
    /// future large reads can split across chunks without changing this
    /// API. The Bulk lane is dedicated so large payloads can never head-of-
    /// line block the small/medium Query lane or per-task Task streams.
    /// </summary>
    private async Task RespondToBulkAsync(string requestId, string resultJson, string mimeType = "application/json")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(resultJson);
        if (await _grpcBulkStream.TrySendSingleChunkAsync(requestId, mimeType, bytes))
            return;

        _logger.LogWarning(
            "Failed to report bulk result for {RequestId} — OpenBulk stream not registered",
            requestId);
    }

    /// <summary>
    /// Send a single drain message over the per-task gRPC OpenTask stream
    /// when one is open for the message's correlation; otherwise return
    /// false so the caller falls back to the SignalR hub method invocation.
    /// Only ProcessOutput / ProcessCompleted reports flow through this path —
    /// other message types (filesystem reports, watcher events) keep going
    /// over SignalR until those streams are wired separately.
    /// </summary>
    private async Task<bool> TrySendUplinkViaGrpcAsync(
        long seq,
        string type,
        string payload,
        Guid? correlationId,
        CancellationToken ct)
    {
        if (correlationId is not Guid corr) return false;
        if (!_grpcTaskStreams.IsOpen(corr)) return false;

        GrpcContracts.TaskClientMessage? msg = type switch
        {
            "ReportProcessOutput" => new GrpcContracts.TaskClientMessage
            {
                Output = new GrpcContracts.ProcessOutput
                {
                    Sequence = seq,
                    PayloadJson = payload,
                },
            },
            "ReportProcessCompleted" => new GrpcContracts.TaskClientMessage
            {
                Completed = new GrpcContracts.ProcessCompleted
                {
                    Sequence = seq,
                    PayloadJson = payload,
                },
            },
            _ => null,
        };
        if (msg is null) return false;

        return await _grpcTaskStreams.TrySendUpAsync(corr, msg, ct);
    }

    /// <summary>
    /// Drains <see cref="GrpcTaskStreamManager.Commands"/> and dispatches each
    /// inbound <c>ServerTaskCommand</c> through the same handlers as
    /// <see cref="OnReceiveMessageAsync"/>. Per-task ack flows on the gRPC
    /// stream only (post-PR #6 the dual SignalR <c>Acknowledge</c> is gone);
    /// the API-side OpenTask Ack handler now flips
    /// <c>OutboxMessage.Status = Acknowledged</c> per-correlation on the
    /// same cumulative ack. The legacy per-machine sequence dedup is reused
    /// so the runner won't double-execute messages on transient duplicate
    /// delivery.
    /// </summary>
    private async Task DispatchGrpcTaskCommandsLoopAsync(CancellationToken ct)
    {
        await foreach (var (correlationId, command) in _grpcTaskStreams.Commands.ReadAllAsync(ct))
        {
            var seq = command.Sequence;

            var lastAcked = Interlocked.Read(ref _lastAckedBackendSequence);
            if (seq <= lastAcked)
            {
                // Duplicate — re-ack so the server's per-correlation cursor
                // can catch up if it lost an earlier ack write.
                await _grpcTaskStreams.TrySendUpAsync(correlationId,
                    new GrpcContracts.TaskClientMessage
                    {
                        Ack = new GrpcContracts.Ack { CumulativeSeq = seq },
                    }, ct);
                continue;
            }

            try
            {
                switch (command.CommandCase)
                {
                    case GrpcContracts.ServerTaskCommand.CommandOneofCase.Start:
                        HandleStartProcess(command.Start.PayloadJson);
                        break;
                    case GrpcContracts.ServerTaskCommand.CommandOneofCase.Stdin:
                        await HandleWriteStdinAsync(command.Stdin.PayloadJson);
                        break;
                    case GrpcContracts.ServerTaskCommand.CommandOneofCase.Kill:
                        HandleKillProcess(command.Kill.PayloadJson);
                        break;
                    default:
                        _logger.LogWarning(
                            "Unknown ServerTaskCommand case {Case} for correlation {CorrelationId}",
                            command.CommandCase, correlationId);
                        continue;
                }

                Interlocked.Exchange(ref _lastAckedBackendSequence, seq);
                await _outbox.SetLastAckedBackendSequenceAsync(seq);

                // Single ack — only the per-correlation gRPC ack. The
                // API-side OpenTask Ack handler now flips per-machine
                // OutboxMessage.Status to Acknowledged on the same
                // cumulative ack (PR #6), so the SignalR Acknowledge that
                // used to do that is no longer needed.
                await _grpcTaskStreams.TrySendUpAsync(correlationId,
                    new GrpcContracts.TaskClientMessage
                    {
                        Ack = new GrpcContracts.Ack { CumulativeSeq = seq },
                    }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error dispatching gRPC task command seq {Seq} for correlation {CorrelationId}",
                    seq, correlationId);
            }
        }
    }

    private void HandleStartProcess(string payloadJson)
    {
        var msg = JsonSerializer.Deserialize<StartProcessMessage>(payloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize StartProcessMessage");

        // Open a per-task gRPC stream for this correlation (fire-and-forget).
        // Once the API-side OutboxProcessorService sees IsOpen() == true for
        // (machine, correlation), subsequent commands for this task flow over
        // the gRPC OpenTask stream instead of the shared SignalR outbox path.
        // The first few commands may still arrive over SignalR while the
        // stream is opening — that's acceptable; both transports drive the
        // same handlers below.
        _ = _grpcTaskStreams.EnsureOpenAsync(msg.CorrelationId);

        var arguments = msg.Arguments ?? new Dictionary<string, string?>();

        // Mintokei MCP injection lives entirely on the API side (in the per-agent
        // ExecutionService.BuildCliOptions). The runner just relays the launch
        // config it receives.

        var options = new CommandLineOptions
        {
            Executable = msg.Executable,
            Arguments = arguments,
            ArgumentList = msg.ArgumentList,
            WorkingDirectory = msg.WorkingDirectory,
            EnvironmentVariables = msg.EnvironmentVariables,
            RedirectStdIn = msg.RedirectStdIn,
            CaptureStdErr = msg.CaptureStdErr,
        };

        var (handle, output) = _commandLineRunner.Start(options);
        _handles[msg.CorrelationId] = handle;

        _logger.LogInformation("Started process for correlation {CorrelationId}: {Executable}",
            msg.CorrelationId, msg.Executable);

        // Stream output in background, feeding each line to the outbox
        _ = StreamProcessOutputAsync(msg.CorrelationId, handle, output);
    }

    private async Task StreamProcessOutputAsync(
        Guid correlationId, IProcessHandle handle, IAsyncEnumerable<CommandOutput> output)
    {
        try
        {
            await foreach (var line in output)
            {
                var outputType = line.Type == OutputType.StdErr ? "StdErr" : "StdOut";
                var report = new ProcessOutputReport(correlationId, line.Line, outputType, line.Timestamp);
                var json = JsonSerializer.Serialize(report, JsonOptions);
                await _outbox.InsertAsync("ReportProcessOutput", json, correlationId);
                SignalDrain();
            }

            // Output stream finished — process has exited
            await handle.WaitForExitAsync();
            var exitCode = handle.ExitCode ?? -1;

            _logger.LogInformation("Process exited with code {ExitCode} for correlation {CorrelationId}",
                exitCode, correlationId);

            var completedReport = new ProcessCompletedReport(correlationId, exitCode, DateTimeOffset.UtcNow);
            var completedJson = JsonSerializer.Serialize(completedReport, JsonOptions);
            await _outbox.InsertAsync("ReportProcessCompleted", completedJson, correlationId);
            SignalDrain();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error streaming output for correlation {CorrelationId}", correlationId);

            // Report failure
            try
            {
                handle.Kill();
                var failedReport = new ProcessCompletedReport(correlationId, -1, DateTimeOffset.UtcNow);
                var failedJson = JsonSerializer.Serialize(failedReport, JsonOptions);
                await _outbox.InsertAsync("ReportProcessCompleted", failedJson, correlationId);
                SignalDrain();
            }
            catch (Exception killEx)
            {
                _logger.LogWarning(killEx, "Failed to report process failure for correlation {CorrelationId}", correlationId);
            }
        }
        finally
        {
            _handles.TryRemove(correlationId, out _);
            await handle.DisposeAsync();
        }
    }

    private async Task HandleWriteStdinAsync(string payloadJson)
    {
        var msg = JsonSerializer.Deserialize<WriteStdinMessage>(payloadJson, JsonOptions);
        if (msg is null) return;

        if (_handles.TryGetValue(msg.CorrelationId, out var handle))
        {
            try
            {
                await handle.WriteAsync(msg.Text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write stdin for correlation {CorrelationId}", msg.CorrelationId);
            }
        }
        else
        {
            // Process no longer exists on this runner (e.g. runner was restarted).
            // Report it as completed so the API cleans up the stale handle and
            // starts a fresh process on the next message.
            _logger.LogWarning(
                "Received WriteStdin for unknown correlation {CorrelationId}, reporting as exited",
                msg.CorrelationId);

            var report = new ProcessCompletedReport(msg.CorrelationId, -1, DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(report, JsonOptions);
            await _outbox.InsertAsync("ReportProcessCompleted", json, msg.CorrelationId);
            SignalDrain();
        }
    }

    private void HandleKillProcess(string payloadJson)
    {
        var msg = JsonSerializer.Deserialize<KillProcessMessage>(payloadJson, JsonOptions);
        if (msg is null) return;

        if (_handles.TryGetValue(msg.CorrelationId, out var handle))
        {
            try
            {
                handle.Kill();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill process for correlation {CorrelationId}", msg.CorrelationId);
            }
        }
    }

    private void SignalDrain() => _drainSignal.Writer.TryWrite(true);

    // =====================================================================
    // File watcher handlers
    // =====================================================================

    private async Task OnFileSystemChangedAsync(string workspaceId)
    {
        // Send via the dedicated OpenWatcher uplink. fs notifications never
        // get HoL-blocked there. If the watcher stream isn't registered, the
        // notification is dropped — the next change event will retry, and
        // any subscribers that missed updates will refresh on the next
        // user-initiated load anyway (workspace events are best-effort).
        var sentViaGrpc = await _grpcWatcherStream.TrySendAsync(new GrpcContracts.WatcherClientMessage
        {
            Changed = new GrpcContracts.FileSystemChanged { WorkspaceId = workspaceId },
        });
        if (sentViaGrpc) return;

        _logger.LogDebug(
            "FileSystemChanged for workspace {WorkspaceId} dropped — OpenWatcher stream not registered",
            workspaceId);
    }

    // =====================================================================
    // Filesystem query handlers (unchanged — these are stateless RPC calls)
    // =====================================================================

    private async Task OnBrowseFilesystemAsync(string requestId, string? path)
    {
        _logger.LogDebug("BrowseFilesystem query: requestId={RequestId}, path={Path}", requestId, path);

        BrowseFilesystemResponse response;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                response = ListDrives(requestId);
            }
            else
            {
                if (path.Contains(".."))
                {
                    response = new BrowseFilesystemResponse(requestId, path, null, [], "Path must not contain '..'.");
                }
                else
                {
                    var fullPath = Path.GetFullPath(path);
                    if (!Directory.Exists(fullPath))
                    {
                        response = new BrowseFilesystemResponse(requestId, path, null, [], "Directory does not exist.");
                    }
                    else
                    {
                        var entries = new List<FilesystemEntry>();
                        try
                        {
                            foreach (var dir in Directory.GetDirectories(fullPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                                entries.Add(new FilesystemEntry(Path.GetFileName(dir), dir, "folder"));
                        }
                        catch (UnauthorizedAccessException) { }
                        catch (IOException) { }

                        var parentPath = Directory.GetParent(fullPath)?.FullName;
                        response = new BrowseFilesystemResponse(requestId, fullPath, parentPath, entries, null);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            response = new BrowseFilesystemResponse(requestId, path ?? "", null, [], ex.Message);
        }

        var json = JsonSerializer.Serialize(response, JsonOptions);
        await RespondToQueryAsync(requestId, json);
    }

    private async Task OnDiscoverGitRepositoriesAsync(string requestId, string path)
    {
        _logger.LogDebug("DiscoverGitRepositories query: requestId={RequestId}, path={Path}", requestId, path);

        DiscoverGitRepositoriesResponse response;
        try
        {
            if (!Directory.Exists(path))
            {
                response = new DiscoverGitRepositoriesResponse(requestId, [], "Directory does not exist.");
            }
            else
            {
                var baseDir = Path.GetFullPath(path);
                var repos = new List<GitRepositoryInfo>();

                var gitDirs = FindGitRepositories(baseDir);
                foreach (var repoDir in gitDirs)
                {
                    var branch = await RunGitCommandAsync(repoDir, "rev-parse --abbrev-ref HEAD") ?? "HEAD";
                    var remoteUrl = await RunGitCommandAsync(repoDir, "remote get-url origin");

                    var relativePath = Path.GetRelativePath(baseDir, repoDir).Replace('\\', '/');
                    if (relativePath == ".") relativePath = "";
                    var name = string.IsNullOrEmpty(relativePath)
                        ? Path.GetFileName(baseDir)
                        : relativePath.Split('/')[^1];

                    repos.Add(new GitRepositoryInfo(name, branch, string.IsNullOrWhiteSpace(remoteUrl) ? null : remoteUrl, relativePath));
                }

                response = new DiscoverGitRepositoriesResponse(requestId, repos, null);
            }
        }
        catch (Exception ex)
        {
            response = new DiscoverGitRepositoriesResponse(requestId, [], ex.Message);
        }

        var json = JsonSerializer.Serialize(response, JsonOptions);
        await RespondToQueryAsync(requestId, json);
    }

    private static BrowseFilesystemResponse ListDrives(string requestId)
    {
        if (OperatingSystem.IsWindows())
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new FilesystemEntry(d.Name, d.RootDirectory.FullName, "drive"))
                .ToList();
            return new BrowseFilesystemResponse(requestId, "", null, drives, null);
        }

        var entries = new List<FilesystemEntry>();
        try
        {
            foreach (var dir in Directory.GetDirectories("/").OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                entries.Add(new FilesystemEntry(Path.GetFileName(dir), dir, "folder"));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return new BrowseFilesystemResponse(requestId, "/", null, entries, null);
    }

    private static List<string> FindGitRepositories(string baseDir)
    {
        var result = new List<string>();

        if (IsGitRepository(baseDir))
            result.Add(baseDir);

        try
        {
            foreach (var subDir in Directory.GetDirectories(baseDir))
            {
                if (Path.GetFileName(subDir).StartsWith('.')) continue;
                if (IsGitRepository(subDir))
                    result.Add(subDir);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return result;
    }

    private static bool IsGitRepository(string dir)
    {
        var gitPath = Path.Combine(dir, ".git");
        return Directory.Exists(gitPath) || File.Exists(gitPath);
    }

    private static async Task<string?> RunGitCommandAsync(string workingDirectory, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task OnRunCommandAsync(string requestId, string workingDirectory, string executable, string arguments, int timeoutMs)
    {
        _logger.LogDebug("RunCommand query: requestId={RequestId}, exe={Exe}, args={Args}, dir={Dir}",
            requestId, executable, arguments, workingDirectory);

        RunCommandResponse response;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                response = new RunCommandResponse(requestId, -1, "", "", $"Failed to start process '{executable}'.");
            }
            else
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                try
                {
                    var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                    var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
                    await process.WaitForExitAsync(cts.Token);

                    var stdout = await stdoutTask;
                    var stderr = await stderrTask;

                    const int maxOutputBytes = 1024 * 1024;
                    if (stdout.Length > maxOutputBytes) stdout = stdout[..maxOutputBytes];
                    if (stderr.Length > maxOutputBytes) stderr = stderr[..maxOutputBytes];

                    response = new RunCommandResponse(requestId, process.ExitCode, stdout, stderr, null);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    response = new RunCommandResponse(requestId, -1, "", "", "Command timed out.");
                }
            }
        }
        catch (Exception ex)
        {
            response = new RunCommandResponse(requestId, -1, "", "", ex.Message);
        }

        var json = JsonSerializer.Serialize(response, JsonOptions);
        await RespondToQueryAsync(requestId, json);
    }

    private async Task OnGetDirectoryTreeAsync(string requestId, string basePath, string? subPath, int maxDepth, bool useGitIgnore)
    {
        _logger.LogDebug("GetDirectoryTree query: requestId={RequestId}, basePath={BasePath}, subPath={SubPath}",
            requestId, basePath, subPath);

        GetDirectoryTreeResponse response;
        try
        {
            var baseDir = basePath;
            if (!string.IsNullOrWhiteSpace(subPath))
            {
                if (subPath.Contains(".."))
                {
                    response = new GetDirectoryTreeResponse(requestId, null, "Path traversal is not allowed.");
                    goto send;
                }

                var targetDir = Path.GetFullPath(Path.Combine(baseDir, subPath));
                var normalizedBase = Path.GetFullPath(baseDir);

                if (!targetDir.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
                {
                    response = new GetDirectoryTreeResponse(requestId, null, "Path traversal is not allowed.");
                    goto send;
                }

                if (!Directory.Exists(targetDir))
                {
                    response = new GetDirectoryTreeResponse(requestId, null, "Directory not found.");
                    goto send;
                }

                baseDir = targetDir;
            }
            else if (!Directory.Exists(baseDir))
            {
                response = new GetDirectoryTreeResponse(requestId, null, "Working directory does not exist.");
                goto send;
            }

            var nodes = EnumerateTree(baseDir, 0, maxDepth, useGitIgnore);
            response = new GetDirectoryTreeResponse(requestId, nodes, null);
        }
        catch (Exception ex)
        {
            response = new GetDirectoryTreeResponse(requestId, null, ex.Message);
        }

        send:
        var json = JsonSerializer.Serialize(response, JsonOptions);
        await RespondToQueryAsync(requestId, json);
    }

    private async Task OnFindFileAsync(string requestId, string basePath, string suffix, int limit)
    {
        _logger.LogDebug("FindFile query: requestId={RequestId}, basePath={BasePath}, suffix={Suffix}",
            requestId, basePath, suffix);

        FindFileResponse response;
        try
        {
            if (string.IsNullOrWhiteSpace(suffix))
            {
                response = new FindFileResponse(requestId, null, "The 'suffix' parameter is required.");
            }
            else if (!Directory.Exists(basePath))
            {
                response = new FindFileResponse(requestId, null, "Working directory does not exist.");
            }
            else
            {
                var matches = FileSuffixSearch.Search(basePath, suffix, limit);
                var infos = matches
                    .Select(m => new FindFileMatchInfo(m.Path, m.MatchedSegments, m.Depth))
                    .ToList();
                response = new FindFileResponse(requestId, infos, null);
            }
        }
        catch (Exception ex)
        {
            response = new FindFileResponse(requestId, null, ex.Message);
        }

        var json = JsonSerializer.Serialize(response, JsonOptions);
        await RespondToQueryAsync(requestId, json);
    }

    private async Task OnGetFileContentAsync(string requestId, string basePath, string filePath)
    {
        _logger.LogDebug("GetFileContent query: requestId={RequestId}, basePath={BasePath}, filePath={FilePath}",
            requestId, basePath, filePath);

        const int maxSizeBytes = 512 * 1024;
        const int binaryCheckBytes = 8 * 1024;

        GetFileContentResponse response;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                response = new GetFileContentResponse(requestId, filePath ?? "", null, false, false, 0, "Invalid file path.");
                goto send;
            }

            var fullPath = Path.GetFullPath(Path.Combine(basePath, filePath));

            if (!File.Exists(fullPath))
            {
                response = new GetFileContentResponse(requestId, filePath, null, false, false, 0, "File not found.");
                goto send;
            }

            var fileInfo = new FileInfo(fullPath);
            var fileSizeBytes = fileInfo.Length;

            // Binary detection
            var isBinary = false;
            {
                var buffer = new byte[binaryCheckBytes];
                await using var checkStream = File.OpenRead(fullPath);
                var bytesRead = await checkStream.ReadAsync(buffer.AsMemory(0, binaryCheckBytes));
                for (var i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == 0) { isBinary = true; break; }
                }
            }

            if (isBinary)
            {
                response = new GetFileContentResponse(requestId, filePath, "[Binary file — content not displayed]", true, false, fileSizeBytes, null);
                goto send;
            }

            var isTruncated = fileSizeBytes > maxSizeBytes;
            string content;
            if (isTruncated)
            {
                var readBuffer = new byte[maxSizeBytes];
                await using var stream = File.OpenRead(fullPath);
                var read = await stream.ReadAsync(readBuffer.AsMemory(0, maxSizeBytes));
                content = System.Text.Encoding.UTF8.GetString(readBuffer, 0, read);
            }
            else
            {
                content = await File.ReadAllTextAsync(fullPath, System.Text.Encoding.UTF8);
            }

            response = new GetFileContentResponse(requestId, filePath, content, false, isTruncated, fileSizeBytes, null);
        }
        catch (Exception ex)
        {
            response = new GetFileContentResponse(requestId, filePath ?? "", null, false, false, 0, ex.Message);
        }

        send:
        var json = JsonSerializer.Serialize(response, JsonOptions);
        await RespondToBulkAsync(requestId, json);
    }

    private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    private const long MaxImageBytes = 25L * 1024 * 1024; // 25 MB

    private async Task OnGetImageFileAsync(string requestId, string basePath, string filePath)
    {
        _logger.LogDebug("GetImageFile query: requestId={RequestId}, basePath={BasePath}, filePath={FilePath}",
            requestId, basePath, filePath);

        GetImageFileResponse response;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                response = new GetImageFileResponse(requestId, filePath ?? "", null, null, 0, "Invalid file path.");
                goto send;
            }

            var fullPath = Path.GetFullPath(Path.Combine(basePath, filePath));

            if (!File.Exists(fullPath))
            {
                response = new GetImageFileResponse(requestId, filePath, null, null, 0, "File not found.");
                goto send;
            }

            var ext = Path.GetExtension(fullPath);
            if (!ImageMimeTypes.TryGetValue(ext, out var mime))
            {
                response = new GetImageFileResponse(requestId, filePath, null, null, 0,
                    $"Unsupported image extension '{ext}'.");
                goto send;
            }

            var fileSize = new FileInfo(fullPath).Length;
            if (fileSize > MaxImageBytes)
            {
                response = new GetImageFileResponse(requestId, filePath, null, null, fileSize,
                    $"Image is too large to preview (limit {MaxImageBytes / (1024 * 1024)} MB).");
                goto send;
            }

            var bytes = await File.ReadAllBytesAsync(fullPath);
            response = new GetImageFileResponse(requestId, filePath, bytes, mime, fileSize, null);
        }
        catch (Exception ex)
        {
            response = new GetImageFileResponse(requestId, filePath ?? "", null, null, 0, ex.Message);
        }

        send:
        var json = JsonSerializer.Serialize(response, JsonOptions);
        await RespondToBulkAsync(requestId, json);
    }

    private async Task OnGetFileSizeAsync(string requestId, string path)
    {
        _logger.LogDebug("GetFileSize query: requestId={RequestId}, path={Path}", requestId, path);

        GetFileSizeResponse response;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains(".."))
            {
                response = new GetFileSizeResponse(requestId, path ?? string.Empty, null, "Invalid path.");
            }
            else
            {
                var fi = new FileInfo(path);
                response = fi.Exists
                    ? new GetFileSizeResponse(requestId, path, fi.Length, null)
                    : new GetFileSizeResponse(requestId, path, null, null);
            }
        }
        catch (Exception ex)
        {
            response = new GetFileSizeResponse(requestId, path ?? string.Empty, null, ex.Message);
        }

        var json = JsonSerializer.Serialize(response, JsonOptions);
        await RespondToQueryAsync(requestId, json);
    }

    private async Task OnPathOperationAsync(string requestId, string operation, string[] args)
    {
        _logger.LogDebug("PathOperation query: requestId={RequestId}, operation={Operation}, args={Args}",
            requestId, operation, string.Join(" + ", args));

        ResolvePathResponse response;
        try
        {
            var result = operation switch
            {
                "combine" => Path.GetFullPath(Path.Combine(args)),
                "getFileName" => Path.GetFileName(args[0]),
                "getFullPath" => Path.GetFullPath(args[0]),
                _ => throw new ArgumentException($"Unknown path operation: {operation}"),
            };
            response = new ResolvePathResponse(requestId, result, null);
        }
        catch (Exception ex)
        {
            response = new ResolvePathResponse(requestId, "", ex.Message);
        }

        var json = JsonSerializer.Serialize(response, JsonOptions);
        await RespondToQueryAsync(requestId, json);
    }

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", ".next",
        "dist", "__pycache__", ".venv", "venv", ".tox", "target", "build"
    };

    private static List<DirectoryTreeNode> EnumerateTree(string dirPath, int currentDepth, int maxDepth, bool useGitIgnore)
    {
        var folders = new List<DirectoryTreeNode>();
        var files = new List<DirectoryTreeNode>();

        IEnumerable<string> subDirs;
        try { subDirs = Directory.EnumerateDirectories(dirPath); }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }

        foreach (var subDir in subDirs)
        {
            var dirName = Path.GetFileName(subDir);
            if (SkipDirs.Contains(dirName)) continue;

            List<DirectoryTreeNode>? children = null;
            if (currentDepth + 1 < maxDepth)
            {
                if (useGitIgnore && IsGitRepository(subDir))
                    children = BuildTreeFromGit(subDir, maxDepth - currentDepth - 1);
                else
                    children = EnumerateTree(subDir, currentDepth + 1, maxDepth, useGitIgnore);
            }

            folders.Add(new DirectoryTreeNode(dirName, "folder", children));
        }

        IEnumerable<string> fileEntries;
        try { fileEntries = Directory.EnumerateFiles(dirPath); }
        catch (UnauthorizedAccessException) { return folders; }
        catch (IOException) { return folders; }

        foreach (var file in fileEntries)
            files.Add(new DirectoryTreeNode(Path.GetFileName(file), "file", null));

        folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        folders.AddRange(files);
        return folders;
    }

    private static List<DirectoryTreeNode> BuildTreeFromGit(string gitRepoDir, int maxDepth)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "ls-files --cached --others --exclude-standard",
                WorkingDirectory = gitRepoDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return EnumerateTree(gitRepoDir, 0, maxDepth, false);

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(10));

            if (process.ExitCode != 0) return EnumerateTree(gitRepoDir, 0, maxDepth, false);

            var relativePaths = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().Replace('\\', '/'))
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();

            return BuildTreeFromPaths(relativePaths, maxDepth);
        }
        catch
        {
            return EnumerateTree(gitRepoDir, 0, maxDepth, false);
        }
    }

    private static List<DirectoryTreeNode> BuildTreeFromPaths(List<string> relativePaths, int maxDepth)
    {
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in relativePaths)
        {
            var parts = path.Split('/');
            var current = root;
            var limit = Math.Min(parts.Length, maxDepth + 1);

            for (var i = 0; i < limit; i++)
            {
                var part = parts[i];
                var isFile = i == parts.Length - 1;

                if (isFile)
                {
                    current.TryAdd(part, null!);
                }
                else
                {
                    if (!current.TryGetValue(part, out var existing) || existing is not Dictionary<string, object>)
                    {
                        var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        current[part] = child;
                        current = child;
                    }
                    else
                    {
                        current = (Dictionary<string, object>)existing;
                    }
                }
            }
        }

        return BuildNodes(root);
    }

    private static List<DirectoryTreeNode> BuildNodes(Dictionary<string, object> dict)
    {
        var folders = new List<DirectoryTreeNode>();
        var files = new List<DirectoryTreeNode>();

        foreach (var (name, value) in dict)
        {
            if (value is Dictionary<string, object> children)
                folders.Add(new DirectoryTreeNode(name, "folder", BuildNodes(children)));
            else
                files.Add(new DirectoryTreeNode(name, "file", null));
        }

        folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        folders.AddRange(files);
        return folders;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Kill all running processes on shutdown
        foreach (var (correlationId, handle) in _handles)
        {
            try { handle.Kill(); } catch { }
            try { await handle.DisposeAsync(); } catch { }
            _handles.TryRemove(correlationId, out _);
        }

        await base.StopAsync(cancellationToken);
    }

}
