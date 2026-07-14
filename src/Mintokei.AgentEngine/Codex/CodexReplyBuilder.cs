using System.Text.Json;

using Mintokei.AgentEngine.Contracts;

using Mintokei.AgentEngine.AgentTools.Codex;

namespace Mintokei.AgentEngine.Codex;

/// <summary>
/// Builds the Codex app-server JSON-RPC reply frames that answer a server-originated
/// request — a command/patch approval, a user-input question, or an MCP elicitation —
/// from the user's decision.
///
/// Pure (no process handle / ctx access) and extracted so the wire format lives in one
/// testable place, mirroring <see cref="Claude.ClaudeControlResponseBuilder"/>. The
/// session-cache side effect for elicitations stays with the caller: this only reports
/// (via <c>cacheForSession</c>) whether the caller should remember the approval, so the
/// builder itself never touches the session's approved-MCP-tools cache.
/// </summary>
public static class CodexReplyBuilder
{
    /// <summary>
    /// Reply to <c>item/commandExecution/requestApproval</c> and
    /// <c>item/fileChange/requestApproval</c>.
    /// </summary>
    public static string BuildApprovalReply(string rpcId, UserInteractionResponse decision)
    {
        var codexDecision = decision.Decision switch
        {
            "allow" => "accept",
            "deny" => "decline",
            _ => decision.Decision,
        };

        return Serialize(rpcId, new { decision = codexDecision });
    }

    /// <summary>Reply to <c>item/tool/requestUserInput</c>.</summary>
    public static string BuildUserInputReply(string rpcId, UserInteractionResponse decision)
    {
        object answers = new { };
        if (!string.IsNullOrEmpty(decision.AnswersJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(decision.AnswersJson);
                answers = doc.RootElement.Clone();
            }
            catch { }
        }

        return Serialize(rpcId, new { answers });
    }

    /// <summary>
    /// Reply to <c>mcpServer/elicitation/request</c>. Sets <paramref name="cacheForSession"/>
    /// true when the user accepted with session scope, signalling the caller to remember the
    /// approval in the session's approved-MCP-tools cache (kept out of here so the
    /// builder stays pure).
    /// </summary>
    public static string BuildElicitationReply(string rpcId, UserInteractionResponse decision, out bool cacheForSession)
    {
        var (action, content) = decision.Decision switch
        {
            "allow" or "accept" => ("accept", (object?)new { }),
            "cancel" => ("cancel", (object?)null),
            _ => ("decline", (object?)null),
        };

        cacheForSession = action == "accept" && decision.Scope == "session";
        return Serialize(rpcId, new { action, content });
    }

    /// <summary>Immediate accept for an already-approved (cached) elicitation — no user prompt.</summary>
    public static string BuildElicitationAccept(string rpcId)
        => Serialize(rpcId, new { action = "accept", content = (object)new { } });

    private static string Serialize<TResult>(string rpcId, TResult result)
        => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = int.TryParse(rpcId, out var intId) ? (object)intId : rpcId,
            result,
        }, CodexJsonRpcHelper.JsonOptions);
}
