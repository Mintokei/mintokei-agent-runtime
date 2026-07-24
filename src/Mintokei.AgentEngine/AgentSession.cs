using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.CommandRunner;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine;

/// <summary>
/// Backend-agnostic session engine. Owns one CLI process, runs the output pump, routes the two
/// bidirectional cases (control responses to their waiters, interactions to the policy), and pushes
/// the one-way transcript to a bounded channel exposed as <see cref="Output"/>. All wire-format
/// specifics come from the injected <see cref="IAgentSessionProtocol"/>; all side effects
/// (persistence, event bus, message stream) are the consumer's problem, not this class's.
/// </summary>
public sealed class AgentSession : IAgentSession
{
    // Bounded so a fast CLI can't outrun a slow consumer without back-pressure (parity with the
    // old pump, which awaited each dispatch inline) and can't balloon memory. Not single-writer:
    // besides the pump, the ACP turn-completion path (a separate task awaiting session/prompt) emits
    // through EmitAsync too.
    private const int OutputChannelCapacity = 256;

    private readonly IProcessHandle _handle;
    private readonly IAsyncEnumerable<CommandOutput> _output;
    private readonly IAgentStreamParser _parser;
    private readonly IAgentSessionProtocol _protocol;
    private readonly IInteractionReplyBuilder _replyBuilder;
    private readonly AgentSessionOptions _options;
    private readonly CancellationTokenSource _cts;
    private readonly ILogger _logger;

    private readonly Channel<AgentStreamOutput> _outChannel =
        Channel.CreateBounded<AgentStreamOutput>(new BoundedChannelOptions(OutputChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    // Waiters for control-response round-trips (handshake, mid-session control requests), keyed by request id.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingControlResponses = new();

    // Surfaced interactions awaiting a RespondAsync, keyed by request id.
    private readonly ConcurrentDictionary<string, PendingInteraction> _pendingInteractions = new();

    // "Allow for session" MCP approvals (Codex-style); consulted before surfacing.
    private readonly ConcurrentDictionary<string, bool> _approvedMcpTools = new();

    // Recent stderr, ring-buffered, so a stream that dies mid-handshake fails its waiters with the
    // CLI's own error (auth failure, missing binary, bad flag) rather than a bare "stream ended".
    private const int MaxStderrLines = 40;
    private readonly object _stderrGate = new();
    private readonly Queue<string> _recentStderr = new();

    private Task? _pumpTask;
    private int _nextRequestId;
    private volatile bool _isInterrupted;

    public AgentSession(
        Guid sessionId,
        IProcessHandle handle,
        IAsyncEnumerable<CommandOutput> output,
        IAgentSessionProtocol protocol,
        IInteractionReplyBuilder replyBuilder,
        AgentSessionOptions options,
        CancellationTokenSource cts,
        ILogger logger,
        string? initialAgentSessionId = null)
    {
        SessionId = sessionId;
        _handle = handle;
        _output = output;
        _protocol = protocol;
        _replyBuilder = replyBuilder;
        _options = options;
        _cts = cts;
        _logger = logger;
        AgentSessionId = initialAgentSessionId;
        _parser = protocol.CreateParser(sessionId);
    }

    public Guid SessionId { get; }
    public string? AgentSessionId { get; private set; }
    public bool HasExited => _handle.HasExited;
    public IAsyncEnumerable<AgentStreamOutput> Output => _outChannel.Reader.ReadAllAsync();

    // ── Members the protocol reaches back into ──

    internal IProcessHandle Handle => _handle;

    /// <summary>The live parser (the exact instance the pump reads) — a protocol reaches it for
    /// per-parse bookkeeping, e.g. Claude flagging a user-initiated compaction.</summary>
    internal IAgentStreamParser Parser => _parser;

    internal Task WriteLineAsync(string line, CancellationToken ct) => _handle.WriteLineAsync(line, ct);

    /// <summary>Fires whenever <see cref="CurrentTurnId"/> changes. The prod adapter wires this to
    /// <c>ctx.CurrentTurnId</c> so <c>FinalizePumpAsync</c>'s retry-on-mid-turn-death still works for
    /// the JSON-RPC backends (Codex/ACP) whose turn state now lives on the session, not the context.</summary>
    public Action<string?>? CurrentTurnIdChanged { get; set; }

    private string? _currentTurnId;

    /// <summary>The id of the in-flight turn (JSON-RPC backends), for interrupt params and mid-turn-
    /// death detection. Set by <see cref="BeginTurnAsync"/>; cleared on turn end.</summary>
    internal string? CurrentTurnId
    {
        get => _currentTurnId;
        set
        {
            _currentTurnId = value;
            CurrentTurnIdChanged?.Invoke(value);
        }
    }

    /// <summary>ACP-only. While an <c>session/load</c> replays prior history as <c>session/update</c>
    /// notifications, the pump still feeds them to the parser (to keep its block index consistent) but
    /// suppresses re-emitting the one-way transcript. The protocol flips this around the load call.</summary>
    internal bool IsReplayingHistory { get; set; }

    /// <summary>Records a session/thread id the protocol discovered from a handshake RESPONSE (not a
    /// parsed frame) — updates <see cref="AgentSessionId"/> for subsequent turns and emits a
    /// <see cref="SessionIdChanged"/> so the consumer persists it.</summary>
    internal async Task ReportSessionIdAsync(string sessionId)
    {
        AgentSessionId = sessionId;
        await EmitAsync(new SessionIdChanged(sessionId));
    }

    /// <summary>Marks the start of a streaming turn: records its id and emits the TurnStart delta that
    /// bounds the turn (mirrors the base <c>BeginTurn</c>, but as an Output the consumer publishes).</summary>
    internal async Task BeginTurnAsync(string? turnId)
    {
        CurrentTurnId = turnId;
        await EmitAsync(new DeltaOutput(new TurnPayload(IsStart: true)));
    }

    /// <summary>Monotonic request id for this session. Plain-numeric as a string so it works for both
    /// Claude (string <c>request_id</c>) and JSON-RPC backends (Codex requires an integer id — the
    /// protocol parses this back to int). Also used by protocols for fire-and-forget sends
    /// (interrupt / compact) so their ids never collide with an awaited request's id.</summary>
    internal int NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    /// <summary>Sends a correlated request and awaits its control response (30s cap, like the CLIs
    /// expect). The pump routes the reply back through <see cref="_pendingControlResponses"/>.</summary>
    internal Task<JsonElement> SendRequestAndWaitAsync(string method, object? payload, CancellationToken ct)
        => SendRequestAndWaitAsync(NextRequestId().ToString(), method, payload, TimeSpan.FromSeconds(30), ct);

    /// <summary>Id-controlled + optional-timeout variant. ACP's <c>session/prompt</c> needs a
    /// caller-known id (it IS the turn id, set via <see cref="BeginTurnAsync"/> before the reply
    /// arrives) and NO timeout — a turn runs for minutes, so only the process CTS bounds the wait.
    /// Pass <paramref name="timeout"/> null to disable the cap.</summary>
    internal async Task<JsonElement> SendRequestAndWaitAsync(
        string requestId, string method, object? payload, TimeSpan? timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingControlResponses[requestId] = tcs;
        try
        {
            await _protocol.SendRequestAsync(_handle, requestId, method, payload, ct);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            if (timeout is { } t)
                linked.CancelAfter(t);
            await using var reg = linked.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            _pendingControlResponses.TryRemove(requestId, out _);
        }
    }

    // ── Lifecycle ──

    /// <summary>Starts the pump (so response frames route) then completes the handshake. Called once,
    /// by the factory, before the session is handed to the caller.</summary>
    public async Task StartAsync(bool resume, CancellationToken ct)
    {
        _pumpTask = RunPumpAsync();
        await _protocol.HandshakeAsync(this, resume, ct);
    }

    // Adopted sessions start their request ids here rather than at 0: the adopted CLI may still emit
    // a late response to a request the PREVIOUS api instance sent (its waiter died with that process),
    // and a fresh request re-using a low id would capture that stale reply. Far above any plausible
    // pre-restart request count, and still a plain int for Codex's integer JSON-RPC ids.
    private const int AdoptedRequestIdBase = 1_000_000;

    /// <summary>Adopts an already-initialized process: pump only, no handshake (see
    /// <see cref="IAgentSession.AttachAsync"/>).</summary>
    public Task AttachAsync(CancellationToken ct = default)
    {
        Interlocked.CompareExchange(ref _nextRequestId, AdoptedRequestIdBase, 0);
        _pumpTask = RunPumpAsync();
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(string content, CancellationToken ct = default)
        => SendTurnAsync(new SessionTurn(content), ct);

    public async Task SendTurnAsync(SessionTurn turn, CancellationToken ct = default)
    {
        _isInterrupted = false;
        await _protocol.SendTurnAsync(this, turn, ct);
    }

    public async Task<bool> InterruptAsync(CancellationToken ct = default)
    {
        if (_handle.HasExited)
            return false;
        await _protocol.SendInterruptAsync(this, ct);
        _isInterrupted = true;
        return true;
    }

    public Task CompactAsync(string? instructions, CancellationToken ct = default)
        => _protocol.CompactAsync(this, instructions, ct);

    public Task RollbackAsync(int numTurns, CancellationToken ct = default)
        => _protocol.RollbackAsync(this, numTurns, ct);

    public Task<bool> ApplyConfigAsync(
        Dictionary<string, string?> oldConfig, Dictionary<string, string?> newConfig, CancellationToken ct = default)
        => _protocol.ApplyConfigAsync(this, oldConfig, newConfig, ct);

    public async Task<bool> RespondAsync(string requestId, UserInteractionResponse decision, CancellationToken ct = default)
    {
        if (!_pendingInteractions.TryRemove(requestId, out var pending))
            return false;

        if (decision.Scope == "session" && pending.CacheKey is { } key)
            _approvedMcpTools[key] = true;

        await WriteReplyAsync(pending.Interaction, decision, ct);
        return true;
    }

    // ── Pump ──

    private async Task RunPumpAsync()
    {
        try
        {
            await foreach (var line in _output)
            {
                if (string.IsNullOrWhiteSpace(line.Line))
                    continue;

                if (line.Type != OutputType.StdOut)
                {
                    CaptureStderr(line.Line);
                    AgentSessionLog.Stderr(_logger, line.Line);
                    continue;
                }

                if (!_protocol.TryParseFrame(line.Line, out var frame))
                    continue;

                foreach (var produced in _parser.Parse(SessionId, frame, _isInterrupted))
                {
                    // ACP session/load replay: keep feeding the parser, but suppress re-emitting the
                    // one-way transcript (bidirectional cases still flow). No-op for Claude/Codex.
                    if (IsReplayingHistory && produced is not ControlResponseReceived and not InteractionRequested)
                        continue;
                    await HandleProducedAsync(produced);
                }
            }
        }
        catch (OperationCanceledException)
        {
            AgentSessionLog.PumpCancelled(_logger, SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentSession {SessionId} pump failed", SessionId);
        }
        finally
        {
            _outChannel.Writer.TryComplete();

            var stderr = RecentStderrText();
            foreach (var (id, tcs) in _pendingControlResponses)
            {
                var message = $"Output stream ended before the response to '{id}' arrived.";
                if (stderr is not null)
                    message += $"\nRecent stderr:\n{stderr}";
                tcs.TrySetException(new AgentStreamEndedException(message, id));
            }
            _pendingControlResponses.Clear();
        }
    }

    /// <summary>Emits a one-way output onto <see cref="Output"/>. The pump uses it for parsed frames;
    /// the ACP turn-completion path (which resolves off the <c>session/prompt</c> response, not a
    /// parsed frame) calls it too — hence the channel is not single-writer.</summary>
    internal ValueTask EmitAsync(AgentStreamOutput output) => _outChannel.Writer.WriteAsync(output);

    private void CaptureStderr(string line)
    {
        lock (_stderrGate)
        {
            _recentStderr.Enqueue(line);
            while (_recentStderr.Count > MaxStderrLines)
                _recentStderr.Dequeue();
        }
    }

    private string? RecentStderrText()
    {
        lock (_stderrGate)
            return _recentStderr.Count == 0 ? null : string.Join("\n", _recentStderr);
    }

    private async Task HandleProducedAsync(AgentStreamOutput produced)
    {
        switch (produced)
        {
            case ControlResponseReceived r:
                if (_pendingControlResponses.TryRemove(r.Id, out var tcs))
                {
                    var outcome = _protocol.ClassifyControlResponse(r.Raw);
                    if (outcome.Error is not null)
                        tcs.TrySetException(outcome.Error);
                    else
                        tcs.TrySetResult(outcome.Result!.Value);
                }
                return;

            case InteractionRequested q:
                await HandleInteractionAsync(q);
                return;

            case SessionIdChanged s:
                AgentSessionId = s.SessionId;
                await EmitAsync(produced);
                return;

            default:
                await EmitAsync(produced);
                return;
        }
    }

    private async Task HandleInteractionAsync(InteractionRequested q)
    {
        var interaction = q.Message.UserInteraction;
        if (interaction is null)
        {
            // No structured interaction to reply from — surface it and let the consumer figure it out.
            await EmitAsync(q);
            return;
        }

        // Previously "allow for session" tool → auto-accept without prompting.
        if (q.CacheKey is { } cacheKey && _approvedMcpTools.ContainsKey(cacheKey))
        {
            await WriteReplyAsync(interaction, _protocol.AcceptDecision, _cts.Token);
            return;
        }

        switch (_options.InteractionMode)
        {
            case InteractionMode.AutoApprove:
                await WriteReplyAsync(interaction, _protocol.AcceptDecision, _cts.Token);
                return;

            case InteractionMode.Deny:
                await WriteReplyAsync(interaction, _protocol.DenyDecision, _cts.Token);
                return;

            case InteractionMode.Policy when _options.InteractionHandler is { } handler:
                var decision = await handler(new InteractionRequest(q.RequestId, interaction, q.CacheKey));
                if (decision is not null)
                {
                    if (decision.Scope == "session" && q.CacheKey is { } k)
                        _approvedMcpTools[k] = true;
                    await WriteReplyAsync(interaction, decision, _cts.Token);
                    return;
                }
                goto default; // handler declined → surface

            default: // Surface (and Policy with no/void handler)
                _pendingInteractions[q.RequestId] = new PendingInteraction(interaction, q.CacheKey);
                await EmitAsync(q);
                return;
        }
    }

    private async Task WriteReplyAsync(
        UserInteractionData interaction, UserInteractionResponse decision, CancellationToken ct)
    {
        var line = _replyBuilder.Build(interaction, decision);
        if (line is null)
        {
            _logger.LogWarning(
                "No reply could be built for interaction {RequestId} (decision {Decision})",
                interaction.RequestId, decision.Decision);
            return;
        }
        await _handle.WriteLineAsync(line, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { /* already disposed */ }
        _handle.Kill();
        _outChannel.Writer.TryComplete();

        foreach (var tcs in _pendingControlResponses.Values)
            tcs.TrySetCanceled();
        _pendingControlResponses.Clear();
        _pendingInteractions.Clear();

        if (_pumpTask is not null)
        {
            try { await _pumpTask; }
            catch { /* pump teardown errors are non-fatal on dispose */ }
        }

        await _handle.DisposeAsync();
        _cts.Dispose();
    }

    private readonly record struct PendingInteraction(UserInteractionData Interaction, string? CacheKey);
}

internal static partial class AgentSessionLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[stderr] {Line}")]
    public static partial void Stderr(ILogger logger, string line);

    [LoggerMessage(Level = LogLevel.Information, Message = "AgentSession {SessionId} pump cancelled")]
    public static partial void PumpCancelled(ILogger logger, Guid sessionId);
}
