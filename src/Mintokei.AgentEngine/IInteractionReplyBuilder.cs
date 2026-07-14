
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine;

/// <summary>
/// Rebuilds the wire reply for a persisted interaction on the <em>durable recovery path</em> —
/// when the in-memory turn that would normally write it is gone (e.g. an API restart between the
/// prompt being asked and the user answering, while the agent process keeps running on a runner,
/// blocked on its request). Resolved by
/// <see cref="Mintokei.AgentEngine.AgentTools.AgentToolKey"/> via keyed DI.
///
/// The live path never uses this — it writes via the in-memory reply delegate registered in
/// <c>AgentProcessContext.PendingReplies</c>. This is only the fallback that rebuilds the
/// same reply from the persisted <see cref="UserInteractionData"/> (plus its
/// <see cref="UserInteractionData.ReplyContext"/>).
/// </summary>
public interface IInteractionReplyBuilder
{
    /// <summary>
    /// Builds the reply line to write to the process, or <c>null</c> when this interaction
    /// cannot be rebuilt from persisted state (missing / unrecognised context).
    /// </summary>
    string? Build(UserInteractionData interaction, UserInteractionResponse decision);
}
