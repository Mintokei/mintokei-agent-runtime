using System.Text.Json;
using Mintokei.AgentEngine.Acp;
using Mintokei.AgentEngine.AgentTools.Acp;
using Mintokei.AgentEngine.CommandRunner;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Acp;

/// <summary>
/// ACP (Agent Client Protocol, JSON-RPC over stdio — Copilot / OpenCode) protocol for
/// <see cref="AgentSession"/>. Mirrors <c>AcpExecutionServiceBase</c>: <c>initialize</c> →
/// <c>session/new</c> (fresh) or <c>session/load</c> (resume, with the replay gate) handshake, then
/// per turn a <c>session/prompt</c> whose RESPONSE is the turn completion — awaited off the pump so
/// the send returns immediately. Serves both Copilot and OpenCode; the only per-turn difference is the
/// prompt-params shape (OpenCode threads a per-turn model), injected as <paramref name="buildPromptParams"/>.
/// Compaction is unsupported; config changes need a restart.
/// </summary>
internal sealed class AcpSessionProtocol : IAgentSessionProtocol
{
    private readonly ILogger _logger;
    private readonly string? _cwd;
    private readonly object? _mcpServers;
    private readonly Func<string, IReadOnlyList<object>, object> _buildPromptParams;

    public AcpSessionProtocol(
        ILogger logger,
        string? cwd,
        object? mcpServers,
        Func<string, IReadOnlyList<object>, object>? buildPromptParams = null)
    {
        _logger = logger;
        _cwd = cwd;
        _mcpServers = mcpServers;
        // Copilot: {sessionId, prompt}. OpenCode overrides to add _meta.opencode.modelId.
        _buildPromptParams = buildPromptParams ?? ((sessionId, prompt) => new { sessionId, prompt });
    }

    public IAgentStreamParser CreateParser(Guid sessionId) => new AcpSessionUpdateParser(_logger, _cwd);

    public bool TryParseFrame(string line, out JsonElement frame) => AcpJsonRpcHelper.TryParseJsonRpc(line, out frame);

    public ControlResponseOutcome ClassifyControlResponse(JsonElement raw)
    {
        if (raw.TryGetProperty("error", out var errorProp))
        {
            var errorMessage = errorProp.TryGetProperty("message", out var msgProp)
                ? msgProp.GetString() ?? "Unknown error"
                : "Unknown error";
            var errorCode = errorProp.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : 0;
            return new(null, new AcpException($"ACP error (code {errorCode}): {errorMessage}", errorCode, errorMessage, raw));
        }

        return new(raw, null);
    }

    // AcpReplyBuilder maps "allow"/"deny" (+ scope) to allow_once/reject_once/allow_always/reject_always.
    public UserInteractionResponse AcceptDecision { get; } = new("allow", null, null);
    public UserInteractionResponse DenyDecision { get; } = new("deny", null, null);

    public Task SendRequestAsync(
        IProcessHandle handle, string requestId, string method, object? payload, CancellationToken ct)
        => AcpJsonRpcHelper.SendRequestAsync(handle, int.Parse(requestId), method, payload, ct);

    public async Task HandshakeAsync(AgentSession session, bool resume, CancellationToken ct)
    {
        await session.SendRequestAndWaitAsync("initialize", new
        {
            protocolVersion = 1,
            clientCapabilities = new { fs = new { readTextFile = true, writeTextFile = true } },
        }, ct);

        if (resume && !string.IsNullOrEmpty(session.AgentSessionId))
        {
            var sessionId = session.AgentSessionId!;
            // session/load replays prior history as session/update notifications — gate them off.
            session.IsReplayingHistory = true;
            try
            {
                await session.SendRequestAndWaitAsync("session/load", new { sessionId, cwd = _cwd, mcpServers = _mcpServers }, ct);
            }
            finally
            {
                session.IsReplayingHistory = false;
                ((AcpSessionUpdateParser)session.Parser).Reset();
            }
            await session.ReportSessionIdAsync(sessionId);
            _logger.LogInformation("ACP session loaded: {SessionId}", sessionId);
        }
        else
        {
            var newResponse = await session.SendRequestAndWaitAsync("session/new", new { cwd = _cwd, mcpServers = _mcpServers }, ct);
            var sessionId = AcpJsonRpcHelper.ExtractSessionId(newResponse)
                ?? throw new InvalidOperationException("No sessionId in session/new response.");
            await session.ReportSessionIdAsync(sessionId);
            _logger.LogInformation("ACP session created: {SessionId}", sessionId);
        }
    }

    public async Task SendTurnAsync(AgentSession session, SessionTurn turn, CancellationToken ct)
    {
        var sessionId = session.AgentSessionId
            ?? throw new InvalidOperationException("ACP turn before handshake completed (no sessionId).");

        // Drop any stale buffered text (post-load replay tail, or an abandoned partial turn) so it
        // can't bleed into this turn's first message. Mintokei persists messages independently.
        ((AcpSessionUpdateParser)session.Parser).Reset();

        var prompt = new List<object>();
        if (turn.ContextBlock is { } block)
            prompt.Add(new { type = "text", text = block });
        prompt.Add(new { type = "text", text = turn.Content });
        ImageAttachmentHelper.AppendCopilotImageItems(prompt, turn.ImagesJson);

        // The prompt request id IS the turn id — set it (and emit TurnStart) before firing.
        var promptId = session.NextRequestId().ToString();
        await session.BeginTurnAsync(promptId);

        var promptParams = _buildPromptParams(sessionId, prompt);

        // The turn's stopReason comes back as the session/prompt RESPONSE. A turn runs for minutes,
        // so we can't block the caller on it: await it in the background (no timeout, only the process
        // CTS) and emit the turn-end outputs when it resolves — the second producer into Output.
        _ = Task.Run(async () =>
        {
            try
            {
                var response = await session.SendRequestAndWaitAsync(
                    promptId, "session/prompt", promptParams, timeout: null, CancellationToken.None);
                await CompleteTurnAsync(session, promptId, response, preFailure: null);
            }
            catch (AcpException ex)
            {
                await session.EmitAsync(BuildErrorMessage(session.SessionId, ex.ErrorMessage));
                await CompleteTurnAsync(session, promptId, response: null,
                    preFailure: TurnFailure.FromText(ex.ErrorMessage, TurnFailureKind.ApiError));
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("ACP session/prompt await cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error awaiting ACP session/prompt response");
            }
        });
    }

    /// <summary>Emits the turn-end sequence into Output (flush open parser blocks, then a
    /// <see cref="TurnEnded"/> the consumer turns into the TurnStop delta + TurnCompletedEvent).</summary>
    private static async Task CompleteTurnAsync(AgentSession session, string promptId, JsonElement? response, TurnFailure? preFailure)
    {
        // Close any open text/reasoning block as its own concrete message (preserves narration order).
        if (session.Parser is AcpSessionUpdateParser parser)
        {
            foreach (var produced in parser.FlushPendingBlocks(session.SessionId))
                await session.EmitAsync(produced);
        }

        if (session.CurrentTurnId == promptId)
            session.CurrentTurnId = null;

        var stopReason = response.HasValue ? AcpJsonRpcHelper.ExtractStopReason(response.Value) : null;
        var isInterrupted = stopReason == "cancelled";

        // A captured upstream error wins; otherwise map the stopReason (end_turn = success).
        TurnFailure? failure = isInterrupted ? null : preFailure;
        if (failure is null && !isInterrupted)
        {
            failure = stopReason switch
            {
                "refusal" => new TurnFailure(TurnFailureKind.Refusal, "The agent stopped: the model refused to continue."),
                "max_tokens" => new TurnFailure(TurnFailureKind.MaxTokens, "The agent stopped: the model hit its context/length limit."),
                "max_turn_requests" => new TurnFailure(TurnFailureKind.MaxTurns, "The agent stopped: hit the maximum number of model requests for the turn."),
                _ => null,
            };
        }

        await session.EmitAsync(new TurnEnded(response, isInterrupted, failure));
    }

    public Task SendInterruptAsync(AgentSession session, CancellationToken ct)
        // session/cancel is a fire-and-forget notification; the cancelled turn resolves via stopReason.
        => AcpJsonRpcHelper.SendNotificationAsync(session.Handle, "session/cancel", new { sessionId = session.AgentSessionId }, ct);

    public Task CompactAsync(AgentSession session, string? instructions, CancellationToken ct)
        => throw new NotSupportedException("This agent (ACP) does not expose a compaction trigger.");

    public Task RollbackAsync(AgentSession session, int numTurns, CancellationToken ct)
        => throw new NotSupportedException("This agent (ACP) does not expose a rollback trigger.");

    public Task<bool> ApplyConfigAsync(
        AgentSession session, Dictionary<string, string?> oldConfig, Dictionary<string, string?> newConfig, CancellationToken ct)
        // ACP has no mid-session config channel — changes take effect on the next process restart.
        => Task.FromResult(false);

    private static MessageOutput BuildErrorMessage(Guid agentTaskId, string errorMessage)
        => new(new AgentMessage
        {
            Id = Guid.NewGuid(),
            AgentTaskId = agentTaskId,
            Role = MessageRole.System,
            Type = MessageType.Error,
            Content = errorMessage,
            Status = MessageStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow,
        });
}
