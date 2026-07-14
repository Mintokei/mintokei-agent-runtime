namespace Mintokei.AgentEngine.Contracts;

/// <summary>
/// A caller's decision on a surfaced interaction (permission / question / elicitation). Handed to
/// <c>IAgentSession.RespondAsync</c> and to the keyed <c>IInteractionReplyBuilder</c>, which turn it
/// into the backend's wire reply.
/// </summary>
public record UserInteractionResponse(
    string Decision,
    string? Message,
    string? AnswersJson,
    string? UpdatedPermissionsJson = null,
    bool Interrupt = false,
    string? Scope = null);
