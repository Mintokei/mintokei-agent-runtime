using System.Collections.Concurrent;
using System.Threading.Channels;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Mintokei.Runner.Contracts.Grpc;

namespace Mintokei.Runner;

/// <summary>
/// Per-correlation gRPC OpenTask stream manager. Singleton on the runner.
///
/// Lifecycle:
///   1. <see cref="GrpcRunnerHostedService"/> calls <see cref="RegisterClient"/>
///      after the Control handshake succeeds, supplying the live RunnerLink
///      client + auth headers.
///   2. <see cref="RunnerHostedService"/> (or whoever knows when a process
///      starts) calls <see cref="EnsureOpenAsync"/> with the correlation id.
///      The manager opens an OpenTask bidi stream, sends TaskOpen, starts a
///      reader task that forwards inbound <see cref="ServerTaskCommand"/>s
///      to <see cref="Commands"/> for dispatch by the existing handlers.
///   3. Same caller eventually calls <see cref="CloseAsync"/> (process exit,
///      KillProcess) to complete and tear down the stream.
///   4. On gRPC connection drop, <see cref="GrpcRunnerHostedService"/> calls
///      <see cref="UnregisterClient"/>; all in-flight streams are cancelled
///      and the dictionary is cleared. New EnsureOpenAsync calls return false
///      until the next handshake re-registers.
///
/// This commit only adds the manager and the lifecycle hookup. No caller
/// invokes <see cref="EnsureOpenAsync"/> yet — once a follow-up wires it
/// into HandleStartProcess, downlink for that correlation flows over the
/// per-task gRPC stream automatically (the API-side routing landed in
/// commit 6a055c92).
/// </summary>
public sealed class GrpcTaskStreamManager(ILogger<GrpcTaskStreamManager> logger)
{
    private readonly object _clientLock = new();
    private RunnerLink.RunnerLinkClient? _client;
    private Metadata? _headers;

    private readonly ConcurrentDictionary<Guid, ActiveStream> _streams = new();

    /// <summary>
    /// Per-correlation gates that serialize concurrent <see cref="EnsureOpenAsync"/>
    /// calls. The check-then-act around <c>_streams.ContainsKey</c> isn't
    /// atomic with the actual gRPC open, so without this two callers (e.g.
    /// HandleStartProcess and an OpenTaskRequest landing back-to-back) can
    /// both pass the guard and both spin up a fresh OpenTask call. The API
    /// then races itself in its OpenTask handler on the RunnerOutboxChannels
    /// UNIQUE index, leaving one of the streams unregistered — runner thinks
    /// it opened, API never registered, drain loop sits forever.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _openLocks = new();

    /// <summary>
    /// Inbound commands from the API on any open per-task stream, demuxed
    /// by correlation. Drained by <see cref="RunnerHostedService"/> (in a
    /// follow-up commit) to dispatch StartProcess / WriteStdin / KillProcess
    /// the same way SignalR's ReceiveMessage does.
    /// </summary>
    public ChannelReader<(Guid CorrelationId, ServerTaskCommand Command)> Commands
        => _commandChannel.Reader;

    private readonly Channel<(Guid CorrelationId, ServerTaskCommand Command)> _commandChannel =
        Channel.CreateUnbounded<(Guid, ServerTaskCommand)>(
            new UnboundedChannelOptions { SingleReader = true });

    private sealed class ActiveStream : IDisposable
    {
        public required AsyncDuplexStreamingCall<TaskClientMessage, TaskServerMessage> Call { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        public void Dispose()
        {
            try { Cts.Cancel(); } catch { }
            Call.Dispose();
            WriteLock.Dispose();
            Cts.Dispose();
        }
    }

    public void RegisterClient(RunnerLink.RunnerLinkClient client, Metadata headers)
    {
        lock (_clientLock)
        {
            _client = client;
            _headers = headers;
        }
        logger.LogDebug("GrpcTaskStreamManager: client registered");
    }

    /// <summary>
    /// Called when the gRPC Control connection drops. Cancels every in-flight
    /// per-task stream so their reader tasks unwind and clears the registry.
    /// New <see cref="EnsureOpenAsync"/> calls will return false until a new
    /// client is registered.
    /// </summary>
    public void UnregisterClient()
    {
        lock (_clientLock)
        {
            _client = null;
            _headers = null;
        }

        foreach (var (correlationId, stream) in _streams)
        {
            stream.Dispose();
        }
        _streams.Clear();
        logger.LogDebug("GrpcTaskStreamManager: client unregistered, all task streams cancelled");
    }

    public bool IsOpen(Guid correlationId) => _streams.ContainsKey(correlationId);

    /// <summary>
    /// Snapshot of correlations that currently have an open per-task stream.
    /// Used by <see cref="RunnerHostedService.DrainOutboxLoopAsync"/> to filter
    /// the outbox query so dead correlations from before a restart don't
    /// head-of-line block the drain. Allocates each call — not hot-path.
    /// </summary>
    public IReadOnlyCollection<Guid> OpenCorrelations() => _streams.Keys.ToArray();

    /// <summary>
    /// Opens a new OpenTask bidi stream for this correlation if one isn't
    /// already open, sends the TaskOpen handshake, and starts a reader task
    /// that forwards inbound commands to <see cref="Commands"/>. Returns
    /// false when no client is registered (gRPC connection currently down).
    /// </summary>
    public async Task<bool> EnsureOpenAsync(
        Guid correlationId,
        long lastAckedServerSeq = 0,
        long nextRunnerSeq = 1,
        CancellationToken ct = default)
    {
        if (_streams.ContainsKey(correlationId))
            return true;

        // Serialize EnsureOpenAsync per correlation so back-to-back fire-and-
        // forget callers don't open two parallel gRPC streams (which races
        // the API-side OpenTask handler against the RunnerOutboxChannels
        // unique index). The lock is intentionally per-correlation, so opens
        // for different correlations stay parallel.
        var openLock = _openLocks.GetOrAdd(correlationId, _ => new SemaphoreSlim(1, 1));
        await openLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock — a previous holder may have
            // already opened it.
            if (_streams.ContainsKey(correlationId))
                return true;

        RunnerLink.RunnerLinkClient? client;
        Metadata? headers;
        lock (_clientLock)
        {
            client = _client;
            headers = _headers;
        }
        if (client is null)
            return false;

        var streamCts = new CancellationTokenSource();
        AsyncDuplexStreamingCall<TaskClientMessage, TaskServerMessage>? call = null;
        try
        {
            call = client.OpenTask(headers, cancellationToken: streamCts.Token);

            await call.RequestStream.WriteAsync(new TaskClientMessage
            {
                Open = new TaskOpen
                {
                    TaskCorrelationId = correlationId.ToString(),
                    LastAckedServerSeq = lastAckedServerSeq,
                    NextRunnerSeq = nextRunnerSeq,
                },
            });

            if (!await call.ResponseStream.MoveNext(ct))
            {
                logger.LogWarning("OpenTask {CorrelationId}: server closed before OpenAck", correlationId);
                streamCts.Cancel();
                call.Dispose();
                streamCts.Dispose();
                return false;
            }
            var first = call.ResponseStream.Current;
            if (first.PayloadCase != TaskServerMessage.PayloadOneofCase.OpenAck || !first.OpenAck.Success)
            {
                logger.LogWarning("OpenTask {CorrelationId}: handshake rejected: {Error}",
                    correlationId, first.OpenAck?.Error);
                streamCts.Cancel();
                call.Dispose();
                streamCts.Dispose();
                return false;
            }

            var stream = new ActiveStream { Call = call, Cts = streamCts };
            if (!_streams.TryAdd(correlationId, stream))
            {
                // Lost the race with a concurrent EnsureOpenAsync — tear ours
                // down and let the winner's stream serve future calls.
                stream.Dispose();
                return true;
            }

            // Reader self-cleans via finally → CloseInternal; we don't need
            // to retain the Task on the ActiveStream.
            _ = ReadStreamAsync(correlationId, call.ResponseStream, streamCts.Token);

            logger.LogInformation("OpenTask {CorrelationId}: opened (server lastReceivedRunnerSeq={Seq})",
                correlationId, first.OpenAck.LastReceivedRunnerSeq);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenTask {CorrelationId}: open failed", correlationId);
            streamCts.Cancel();
            call?.Dispose();
            streamCts.Dispose();
            return false;
        }
        }
        finally
        {
            openLock.Release();
        }
    }

    /// <summary>
    /// Send a runner→server message on the open stream. Returns false if no
    /// stream is open for this correlation; the caller should fall back to
    /// the SignalR path. Writes are serialized per-stream because gRPC
    /// stream writers are not threadsafe.
    /// </summary>
    public async Task<bool> TrySendUpAsync(Guid correlationId, TaskClientMessage msg, CancellationToken ct = default)
    {
        if (!_streams.TryGetValue(correlationId, out var stream))
            return false;

        await stream.WriteLock.WaitAsync(ct);
        try
        {
            await stream.Call.RequestStream.WriteAsync(msg);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "OpenTask {CorrelationId}: send failed; tearing down stream so caller falls back to SignalR",
                correlationId);
            CloseInternal(correlationId);
            return false;
        }
        finally
        {
            // The lock may have been disposed by CloseInternal; guard against that.
            try { stream.WriteLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public async Task CloseAsync(Guid correlationId)
    {
        if (!_streams.TryGetValue(correlationId, out var stream))
            return;

        // Half-close: signal end-of-input on the runner→server direction.
        // After this returns, the messages we wrote (including the terminal
        // ReportProcessCompleted) are queued on the wire with the HTTP/2
        // END_STREAM flag set on the request side.
        try { await stream.Call.RequestStream.CompleteAsync(); }
        catch { /* best effort */ }

        // Do NOT call CloseInternal here. CloseInternal disposes the
        // AsyncDuplexStreamingCall, which sends an HTTP/2 RST_STREAM frame.
        // RST_STREAM overrides the END_STREAM flag we just sent and aborts
        // the call before the API server has read our last message off the
        // pipe — empirically this drops the terminal ReportProcessCompleted
        // we just wrote. The API logs
        //   fail: ServerCallHandler[6] Error when executing service method
        //         'OpenTask'. System.IO.IOException: The client reset the
        //         request stream.
        // …the row never lands in InboundRunnerMessages, the dispatcher
        // never runs (so PR #344's CancellationToken.None fix never gets a
        // chance to help), and the one-shot wrapper hangs to its 60s/120s
        // timeout. Shows up most often on the *second* one-shot of a
        // create-task flow (title-gen after branch-name) because the active
        // task's output stream is concurrently using the same gRPC
        // connection, so HTTP/2 buffer contention makes the runner-side
        // RST_STREAM more likely to overtake the in-flight Completed.
        //
        // Cleanup is delegated to ReadStreamAsync's finally{} block: when
        // the API server has finished processing all our buffered uplink
        // messages and returns from OpenTask, gRPC closes the response
        // direction; ReadStreamAsync.MoveNext returns false; its finally
        // calls CloseInternal, which is when _streams loses its entry and
        // the call is disposed.
        //
        // Connection-drop cases are still covered: if the underlying gRPC
        // connection breaks, ReadStreamAsync.MoveNext throws, CloseInternal
        // runs from its finally, and the broader UnregisterClient() also
        // iterates _streams and disposes everything on full disconnect.
        // So no entry can leak in any realistic case.
    }

    private void CloseInternal(Guid correlationId)
    {
        if (_streams.TryRemove(correlationId, out var stream))
        {
            stream.Dispose();
            logger.LogDebug("OpenTask {CorrelationId}: closed", correlationId);
        }
    }

    private async Task ReadStreamAsync(
        Guid correlationId,
        IAsyncStreamReader<TaskServerMessage> reader,
        CancellationToken ct)
    {
        try
        {
            while (await reader.MoveNext(ct))
            {
                var msg = reader.Current;
                if (msg.PayloadCase == TaskServerMessage.PayloadOneofCase.Command)
                {
                    await _commandChannel.Writer.WriteAsync((correlationId, msg.Command), ct);
                }
                // Acks from the server can be observed for telemetry but
                // currently require no action on the runner side.
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenTask {CorrelationId}: reader loop errored", correlationId);
        }
        finally
        {
            CloseInternal(correlationId);
        }
    }
}
