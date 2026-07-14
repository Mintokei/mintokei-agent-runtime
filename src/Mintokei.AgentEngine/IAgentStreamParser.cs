using System.Text.Json;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine;

/// <summary>
/// A backend's stream parser: turns one already-parsed JSON frame into zero or more
/// <see cref="AgentStreamOutput"/> values — the one-way transcript plus the bidirectional
/// <see cref="ControlResponseReceived"/> / <see cref="InteractionRequested"/> cases — as pure
/// data. The base output pump (<c>AgentExecutionServiceBase.RunOutputStreamAsync</c>) holds these
/// by task id and calls <see cref="Parse"/> once per stdout frame.
///
/// Pure with respect to process state: the output pump passes in the agent-task id and interrupt
/// flag explicitly rather than having the parser reach back into session state.
/// A backend implements this explicitly and keeps its own richer public entry point
/// (Claude/Codex <c>Consume</c>, ACP <c>Parse(id, frame)</c>) for its unit tests.
/// </summary>
public interface IAgentStreamParser
{
    /// <summary>
    /// Parses one stdout frame into zero or more normalized outputs.
    /// </summary>
    /// <param name="agentTaskId">
    /// Session-scoped id used to tag emitted messages. ACP uses it directly; other parsers may ignore it.
    /// </param>
    /// <param name="frame">The already-parsed JSON frame from the CLI.</param>
    /// <param name="isInterrupted">
    /// Whether the current turn is being interrupted; parsers that classify turn ends can use it.
    /// </param>
    IEnumerable<AgentStreamOutput> Parse(Guid agentTaskId, JsonElement frame, bool isInterrupted);
}
