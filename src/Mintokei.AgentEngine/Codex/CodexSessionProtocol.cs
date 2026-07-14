using System.Text.Json;
using Mintokei.AgentEngine.Codex;
using Mintokei.AgentEngine.AgentTools.Codex;
using Mintokei.AgentEngine.CommandRunner;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Codex;

/// <summary>
/// Codex app-server (JSON-RPC over stdio) protocol for <see cref="AgentSession"/>. Reuses the
/// existing <see cref="CodexStreamParser"/>, <see cref="CodexRequestParamsBuilder"/> and
/// <see cref="CodexJsonRpcHelper"/>; the wire specifics mirror <c>CodexAppServerExecutionService</c>:
/// <c>initialize</c> → <c>initialized</c> notification → <c>thread/start|resume</c> handshake, then
/// <c>turn/start</c> per turn (completion arrives later as a streamed <c>turn/completed</c> the parser
/// turns into <c>TurnEnded</c>). Request ids are integers on the wire — the session hands us a numeric
/// string id which we parse back to int.
/// </summary>
internal sealed class CodexSessionProtocol : IAgentSessionProtocol
{
    private readonly ILogger _logger;
    private readonly CodexConfigMapper.MappedConfig _config;
    private readonly string? _systemPrompt;

    // Turn-level config overlay applied to every turn/start. Seeded from the mapped config and
    // refreshed by ApplyConfigAsync so mid-session model/effort changes take effect on the next turn.
    private CodexConfigMapper.TurnStartConfig? _turnConfig;

    public CodexSessionProtocol(ILogger logger, CodexConfigMapper.MappedConfig config, string? systemPrompt)
    {
        _logger = logger;
        _config = config;
        _systemPrompt = systemPrompt;
        _turnConfig = config.TurnStart;
    }

    public IAgentStreamParser CreateParser(Guid sessionId) => new CodexStreamParser(_logger, sessionId);

    public bool TryParseFrame(string line, out JsonElement frame) => CodexJsonRpcHelper.TryParseJsonRpc(line, out frame);

    public ControlResponseOutcome ClassifyControlResponse(JsonElement raw)
    {
        if (raw.TryGetProperty("error", out var errorProp))
        {
            var errorMessage = errorProp.TryGetProperty("message", out var msgProp)
                ? msgProp.GetString() ?? "Unknown error"
                : "Unknown error";
            var errorCode = errorProp.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : 0;
            return new(null, new CodexJsonRpcException(
                $"Codex JSON-RPC error (code {errorCode}): {errorMessage}", errorCode, errorMessage, raw));
        }

        return new(raw, null);
    }

    // Codex's reply builders map "allow"/"deny" to their wire vocabulary (approval accept/decline,
    // elicitation accept/decline) — see CodexReplyBuilder.
    public UserInteractionResponse AcceptDecision { get; } = new("allow", null, null);
    public UserInteractionResponse DenyDecision { get; } = new("deny", null, null);

    public Task SendRequestAsync(
        IProcessHandle handle, string requestId, string method, object? payload, CancellationToken ct)
        => CodexJsonRpcHelper.SendRequestAsync(handle, int.Parse(requestId), method, payload, ct);

    public async Task HandshakeAsync(AgentSession session, bool resume, CancellationToken ct)
    {
        // Re-inject the snapshotted system prompt so updated prompts take effect on resume too.
        _config.ThreadStart.BaseInstructions = _systemPrompt;

        await session.SendRequestAndWaitAsync("initialize", new
        {
            protocolVersion = "2025-01-01",
            capabilities = new { experimentalApi = true },
            clientInfo = new { name = "mintokei", version = "0.1.0" },
        }, ct);

        await CodexJsonRpcHelper.SendNotificationAsync(session.Handle, "initialized", null, ct);

        var resuming = resume && !string.IsNullOrEmpty(session.AgentSessionId);
        var threadResponse = resuming
            ? await session.SendRequestAndWaitAsync(
                "thread/resume",
                CodexRequestParamsBuilder.BuildThreadResumeParams(session.AgentSessionId!, _config.ThreadStart), ct)
            : await session.SendRequestAndWaitAsync(
                "thread/start",
                CodexRequestParamsBuilder.BuildThreadStartParams(_config.ThreadStart), ct);

        var threadId = CodexJsonRpcHelper.ExtractThreadId(threadResponse)
            ?? throw new InvalidOperationException("No threadId in thread/start|resume response.");
        await session.ReportSessionIdAsync(threadId);
        _logger.LogDebug("Codex thread {ThreadId} (resume={Resume})", threadId, resuming);
    }

    public async Task SendTurnAsync(AgentSession session, SessionTurn turn, CancellationToken ct)
    {
        var threadId = session.AgentSessionId
            ?? throw new InvalidOperationException("Codex turn before handshake completed (no threadId).");

        var inputItems = new List<object>();
        // Codex sends the workspace context block as a separate input item (not inlined like Claude).
        if (turn.ContextBlock is { } block)
            inputItems.Add(new { type = "text", text = block });
        inputItems.Add(new { type = "text", text = turn.Content });
        ImageAttachmentHelper.AppendCodexImageItems(inputItems, turn.ImagesJson);

        var response = await session.SendRequestAndWaitAsync(
            "turn/start", CodexRequestParamsBuilder.BuildTurnStartParams(threadId, inputItems, _turnConfig), ct);
        await session.BeginTurnAsync(CodexJsonRpcHelper.ExtractTurnId(response));
    }

    public Task SendInterruptAsync(AgentSession session, CancellationToken ct)
        // Fire-and-forget: Codex acks the interrupt on the stream, not via a routed response.
        => CodexJsonRpcHelper.SendRequestAsync(session.Handle, session.NextRequestId(), "turn/interrupt", new
        {
            threadId = session.AgentSessionId,
            turnId = session.CurrentTurnId,
        }, ct);

    public Task CompactAsync(AgentSession session, string? instructions, CancellationToken ct)
    {
        // Codex's thread/compact/start takes only {threadId} — custom instructions are ignored.
        if (!string.IsNullOrWhiteSpace(instructions))
            _logger.LogDebug("Codex ignores custom compaction instructions — using default summarization");

        ((CodexStreamParser)session.Parser).NoteManualCompact();
        return CodexJsonRpcHelper.SendRequestAsync(session.Handle, session.NextRequestId(), "thread/compact/start", new
        {
            threadId = session.AgentSessionId,
        }, ct);
    }

    public Task RollbackAsync(AgentSession session, int numTurns, CancellationToken ct)
        // Rewind-in-place: drops the last numTurns turns from the live thread. Awaited like any
        // other round-trip — the session's pump routes the response to the waiter.
        => session.SendRequestAndWaitAsync("thread/rollback", new
        {
            threadId = session.AgentSessionId,
            numTurns,
        }, ct);

    public Task<bool> ApplyConfigAsync(
        AgentSession session, Dictionary<string, string?> oldConfig, Dictionary<string, string?> newConfig, CancellationToken ct)
    {
        // Only turn-level settings apply mid-session (thread settings need a restart); refresh the overlay.
        _turnConfig = CodexConfigMapper.Map(newConfig).TurnStart;
        return Task.FromResult(true);
    }
}
