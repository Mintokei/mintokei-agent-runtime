
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine;

/// <summary>
/// How an <see cref="IAgentSession"/> answers the permission / question / elicitation requests the
/// CLI raises mid-turn. The session owns the wire mechanics (build the reply via the keyed
/// <see cref="IInteractionReplyBuilder"/>, write it to the process, correlate by request id); this
/// only picks <em>who decides</em>.
/// </summary>
public enum InteractionMode
{
    /// <summary>Emit the request on <see cref="IAgentSession.Output"/> and wait for the caller to
    /// answer via <see cref="IAgentSession.RespondAsync"/>. The interactive default — lets a human
    /// decide minutes later without stalling the read pump.</summary>
    Surface,

    /// <summary>Auto-accept every request inline. Headless / CI / fully-trusted runs.</summary>
    AutoApprove,

    /// <summary>Auto-reject every request inline.</summary>
    Deny,

    /// <summary>Consult <see cref="AgentSessionOptions.InteractionHandler"/>; a returned decision is
    /// written inline, <c>null</c> falls back to <see cref="Surface"/>. The handler runs on the pump
    /// thread, so it must be fast — anything that waits on a human must use <see cref="Surface"/>.</summary>
    Policy,
}

/// <summary>Construction-time options for an <see cref="IAgentSession"/>.</summary>
public sealed class AgentSessionOptions
{
    /// <summary>How to handle interaction requests. Defaults to <see cref="InteractionMode.Surface"/>.</summary>
    public InteractionMode InteractionMode { get; init; } = InteractionMode.Surface;

    /// <summary>Used only when <see cref="InteractionMode"/> is <see cref="InteractionMode.Policy"/>:
    /// return a decision to auto-answer, or <c>null</c> to surface the request on the output stream.</summary>
    public Func<InteractionRequest, ValueTask<UserInteractionResponse?>>? InteractionHandler { get; init; }
}

/// <summary>
/// A permission / question / elicitation the CLI is blocked on. Handed to an
/// <see cref="AgentSessionOptions.InteractionHandler"/> policy; the same
/// <paramref name="Interaction"/> is what <see cref="IAgentSession.RespondAsync"/> replies to.
/// </summary>
public sealed record InteractionRequest(
    string RequestId,
    UserInteractionData Interaction,
    string? CacheKey);
