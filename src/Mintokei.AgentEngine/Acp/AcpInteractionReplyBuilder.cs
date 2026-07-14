using System.Text.Json;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Acp;

/// <summary>
/// Durable-recovery reply builder for ACP agents (Copilot, OpenCode). The ACP reply needs the
/// options the agent originally offered and whether the JSON-RPC id was numeric — neither is in a
/// dedicated column, so both are read from
/// <see cref="UserInteractionData.ReplyContext"/> (<c>{"options":[…],"useIntId":true}</c>),
/// captured when the permission request was parsed.
/// </summary>
public sealed class AcpInteractionReplyBuilder : IInteractionReplyBuilder
{
    /// <summary>
    /// The ReplyContext the ACP permission parse site persists: the offered options plus whether
    /// the JSON-RPC id was numeric. Writer and reader share this one definition.
    /// </summary>
    public static string BuildContext(JsonElement options, bool useIntId)
        => JsonSerializer.Serialize(new
        {
            options = options.ValueKind == JsonValueKind.Array ? (object)options : null,
            useIntId,
        });

    public string? Build(UserInteractionData interaction, UserInteractionResponse decision)
    {
        if (string.IsNullOrEmpty(interaction.ReplyContext)) return null;

        bool useIntId;
        JsonElement options;
        try
        {
            using var doc = JsonDocument.Parse(interaction.ReplyContext);
            var root = doc.RootElement;
            useIntId = root.TryGetProperty("useIntId", out var u)
                       && u.ValueKind is JsonValueKind.True or JsonValueKind.False
                       && u.GetBoolean();
            options = root.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                ? o.Clone()
                : default;
        }
        catch (JsonException)
        {
            return null;
        }

        return AcpReplyBuilder.Build(interaction.RequestId, useIntId, options, decision);
    }
}
