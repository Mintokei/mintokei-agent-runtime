using System.Text.Json;
using Mintokei.AgentEngine.AgentTools.Acp;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Acp;

/// <summary>
/// Builds the ACP <c>session/request_permission</c> JSON-RPC reply from the user's decision:
/// maps the mintokei decision + scope to an ACP option kind, resolves that to a concrete
/// <c>optionId</c> from the options the agent offered, and serialises the outcome frame.
///
/// Pure (no process handle / ctx access) and extracted so the wire format lives in one
/// testable place, mirroring <see cref="Claude.ClaudeControlResponseBuilder"/>.
/// </summary>
public static class AcpReplyBuilder
{
    /// <param name="rpcIdRaw">The raw request id text.</param>
    /// <param name="useIntId">True when the original id was a JSON number, so it is echoed back as a number.</param>
    /// <param name="options">The offered ACP options array from the request params.</param>
    /// <param name="decision">The user's decision.</param>
    /// <returns>The serialised JSON-RPC response line.</returns>
    public static string Build(string rpcIdRaw, bool useIntId, JsonElement options, UserInteractionResponse decision)
    {
        // Map mintokei decision → ACP option kind, then resolve to a concrete optionId from the offered list.
        var desiredKind = (decision.Decision, decision.Scope) switch
        {
            ("allow", "session") => "allow_always",
            ("allow", _) => "allow_once",
            ("deny", "session") => "reject_always",
            ("deny", _) => "reject_once",
            ("cancel", _) => null, // → cancelled outcome
            _ => "reject_once",
        };

        object outcome = desiredKind is null
            ? new { outcome = "cancelled" }
            : new
            {
                outcome = "selected",
                optionId = PickOptionId(options, desiredKind) ?? PickFallbackOptionId(options, desiredKind),
            };

        var responseObj = new
        {
            jsonrpc = "2.0",
            id = useIntId && int.TryParse(rpcIdRaw, out var asInt) ? (object)asInt : (object)rpcIdRaw.Trim('"'),
            result = new { outcome },
        };

        return JsonSerializer.Serialize(responseObj, AcpJsonRpcHelper.JsonOptions);
    }

    private static string? PickOptionId(JsonElement options, string desiredKind)
    {
        if (options.ValueKind != JsonValueKind.Array) return null;
        foreach (var opt in options.EnumerateArray())
        {
            if (opt.ValueKind != JsonValueKind.Object) continue;
            if (opt.TryGetProperty("kind", out var k)
                && k.ValueKind == JsonValueKind.String
                && k.GetString() == desiredKind
                && opt.TryGetProperty("optionId", out var idp)
                && idp.ValueKind == JsonValueKind.String)
            {
                return idp.GetString();
            }
        }
        return null;
    }

    private static string? PickFallbackOptionId(JsonElement options, string desiredKind)
    {
        if (options.ValueKind != JsonValueKind.Array) return null;
        var wantAllow = desiredKind.StartsWith("allow", StringComparison.Ordinal);

        foreach (var opt in options.EnumerateArray())
        {
            if (opt.ValueKind != JsonValueKind.Object) continue;
            var kind = opt.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            if (kind is null) continue;

            var isAllow = kind.StartsWith("allow", StringComparison.Ordinal);
            if (isAllow != wantAllow) continue;

            if (opt.TryGetProperty("optionId", out var idp) && idp.ValueKind == JsonValueKind.String)
                return idp.GetString();
        }
        return null;
    }
}
