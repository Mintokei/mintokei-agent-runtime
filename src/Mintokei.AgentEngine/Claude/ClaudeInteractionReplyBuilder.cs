
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Claude;

/// <summary>
/// Durable-recovery reply builder for Claude Code. Everything the reply needs already lives in
/// the persisted interaction columns (request id, tool name, questions, tool input), so no
/// <see cref="UserInteractionData.ReplyContext"/> is required.
/// </summary>
public sealed class ClaudeInteractionReplyBuilder : IInteractionReplyBuilder
{
    public string? Build(UserInteractionData interaction, UserInteractionResponse decision)
    {
        var isAskUser = string.Equals(interaction.ToolName, "AskUserQuestion", StringComparison.OrdinalIgnoreCase);
        return ClaudeControlResponseBuilder.Build(
            interaction.RequestId, isAskUser, interaction.Questions, interaction.ToolInput, decision);
    }
}
