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
/// Pure with respect to process state: the two process-derived inputs the pump still feeds are
/// passed explicitly rather than read from the context —
/// <list type="bullet">
///   <item><paramref name="agentTaskId"/> tags produced messages (only the ACP parser needs it;
///   the Claude/Codex parsers capture their id at construction and ignore this),</item>
///   <item><paramref name="isInterrupted"/> is the live <c>AgentProcessContext.IsInterrupted</c>
///   flag the Claude turn-end classification needs (the others ignore it).</item>
/// </list>
/// A backend implements this explicitly and keeps its own richer public entry point
/// (Claude/Codex <c>Consume</c>, ACP <c>Parse(id, frame)</c>) for its unit tests.
/// </summary>
public interface IAgentStreamParser
{
    IEnumerable<AgentStreamOutput> Parse(Guid agentTaskId, JsonElement frame, bool isInterrupted);
}
