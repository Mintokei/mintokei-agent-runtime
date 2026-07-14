using System.Text.Json;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.AgentTools.Claude;
using Mintokei.AgentEngine.CommandRunner;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Claude;

/// <summary>
/// Claude Code (<c>--input-format stream-json</c>) protocol for <see cref="AgentSession"/>. Reuses
/// the existing <see cref="ClaudeStreamParser"/> and <see cref="ClaudeInteractionReplyBuilder"/>;
/// the wire specifics here mirror <c>ClaudeCodeExecutionService</c> exactly (control-request
/// envelope + subtype, wrapped <c>control_response</c> classification, stream-json user message).
/// </summary>
internal sealed class ClaudeSessionProtocol : IAgentSessionProtocol
{
    private readonly ILogger _logger;

    public ClaudeSessionProtocol(ILogger logger) => _logger = logger;

    // Claude's reply builder maps any non-"allow" permission decision to behavior:deny, so accept
    // MUST be "allow" (not "accept") or AutoApprove would silently deny every tool.
    public UserInteractionResponse AcceptDecision { get; } = new("allow", null, null);
    public UserInteractionResponse DenyDecision { get; } = new("deny", null, null);

    public IAgentStreamParser CreateParser(Guid sessionId) => new ClaudeStreamParser(_logger, sessionId);

    public bool TryParseFrame(string line, out JsonElement frame) => ClaudeCodeHelper.TryParseJson(line, out frame);

    public ControlResponseOutcome ClassifyControlResponse(JsonElement raw)
    {
        var response = raw.GetProperty("response");
        if (response.TryGetProperty("subtype", out var subtypeProp) && subtypeProp.GetString() == "error")
        {
            var errorMsg = response.TryGetProperty("error", out var errProp)
                ? errProp.GetString() ?? "Unknown error"
                : "Unknown error";
            return new(null, new InvalidOperationException($"Control request failed: {errorMsg}"));
        }
        return new(response, null);
    }

    public Task SendRequestAsync(
        IProcessHandle handle, string requestId, string method, object? payload, CancellationToken ct)
        => ClaudeCodeHelper.SendControlRequestAsync(handle, requestId, MergeSubtype(method, payload), ct);

    public Task HandshakeAsync(AgentSession session, bool resume, CancellationToken ct)
        // The control_response for "initialize" routes through the pump, already running by now.
        => session.SendRequestAndWaitAsync("initialize", new { hooks = (object?)null }, ct);

    public Task SendTurnAsync(AgentSession session, SessionTurn turn, CancellationToken ct)
    {
        // Inline the first-turn workspace context block (when present), then fold in image
        // attachments — byte-for-byte the stream-json user message ClaudeCodeExecutionService writes.
        var text = turn.ContextBlock is { } block
            ? ContextBlockFormatter.FormatMessageWithContext(block, turn.Content)
            : turn.Content;
        var content = ImageAttachmentHelper.BuildClaudeCodeContent(text, turn.ImagesJson);

        var message = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content },
            session_id = session.AgentSessionId ?? "default",
            parent_tool_use_id = (string?)null,
        }, ClaudeCodeHelper.JsonOptions);

        return session.WriteLineAsync(message, ct);
    }

    public Task SendInterruptAsync(AgentSession session, CancellationToken ct)
    {
        // Fire-and-forget: Claude doesn't ack the interrupt with a routed control_response.
        var requestId = $"req_int_{Guid.NewGuid():N}";
        return ClaudeCodeHelper.SendControlRequestAsync(session.Handle, requestId, new { subtype = "interrupt" }, ct);
    }

    public Task CompactAsync(AgentSession session, string? instructions, CancellationToken ct)
    {
        // Claude's /compact is a slash command over the normal user-message channel. Tag the
        // upcoming compact_boundary as user-initiated on the exact parser the pump reads, then send
        // the message; the compact_boundary + summary events flow back through the pump.
        var content = string.IsNullOrWhiteSpace(instructions) ? "/compact" : $"/compact {instructions}";
        ((ClaudeStreamParser)session.Parser).NoteManualCompact();
        return SendTurnAsync(session, new SessionTurn(content), ct);
    }

    public Task RollbackAsync(AgentSession session, int numTurns, CancellationToken ct)
        // Claude has no in-place rollback — its rewind kills the CLI and respawns with --resume.
        => throw new NotSupportedException("Claude Code has no in-place rollback; rewind restarts the session.");

    public async Task<bool> ApplyConfigAsync(
        AgentSession session, Dictionary<string, string?> oldConfig, Dictionary<string, string?> newConfig, CancellationToken ct)
    {
        var controlRequests = ClaudeCodeConfigMapper.GetControlRequests(oldConfig, newConfig);
        if (controlRequests.Count == 0)
            return false;

        var applied = false;
        foreach (var (subtype, payload) in controlRequests)
        {
            try
            {
                await session.SendRequestAndWaitAsync(subtype, payload, ct);
                applied = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply mid-session {Subtype}", subtype);
            }
        }

        return applied;
    }

    /// <summary>Wraps a control-request payload with its <c>subtype</c> (Claude's request envelope).</summary>
    private static object MergeSubtype(string subtype, object? payload)
    {
        var dict = new Dictionary<string, object?> { ["subtype"] = subtype };
        if (payload is null)
            return dict;

        var json = JsonSerializer.Serialize(payload, ClaudeCodeHelper.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        return dict;
    }
}
