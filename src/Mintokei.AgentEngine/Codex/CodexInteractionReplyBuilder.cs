using System.Text.Json;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Codex;

/// <summary>
/// Durable-recovery reply builder for the Codex app-server. The request id is the persisted
/// <see cref="UserInteractionData.RequestId"/>; the sub-kind (which of the three
/// server-originated request shapes it was) is read from
/// <see cref="UserInteractionData.ReplyContext"/>. The parse sites persist that
/// context via the <c>*Context</c> values below, so writer and reader share one definition.
/// </summary>
public sealed class CodexInteractionReplyBuilder : IInteractionReplyBuilder
{
    private const string Approval = "approval";
    private const string UserInput = "userInput";
    private const string Elicitation = "elicitation";

    /// <summary>ReplyContext the Codex parse sites persist for each request sub-kind.</summary>
    public static readonly string ApprovalContext = Serialize(Approval);
    public static readonly string UserInputContext = Serialize(UserInput);
    public static readonly string ElicitationContext = Serialize(Elicitation);

    public string? Build(UserInteractionData interaction, UserInteractionResponse decision) =>
        ReadKind(interaction.ReplyContext) switch
        {
            Approval => CodexReplyBuilder.BuildApprovalReply(interaction.RequestId, decision),
            UserInput => CodexReplyBuilder.BuildUserInputReply(interaction.RequestId, decision),
            // The session-cache flag is moot here: the in-memory ApprovedMcpTools cache died with
            // the process, so we only need the wire reply.
            Elicitation => CodexReplyBuilder.BuildElicitationReply(interaction.RequestId, decision, out _),
            _ => null,
        };

    private static string Serialize(string kind) => JsonSerializer.Serialize(new { kind });

    private static string? ReadKind(string? replyContext)
    {
        if (string.IsNullOrEmpty(replyContext)) return null;
        try
        {
            using var doc = JsonDocument.Parse(replyContext);
            return doc.RootElement.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
