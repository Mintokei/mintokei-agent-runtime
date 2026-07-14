using System.Text.Json;

namespace Mintokei.AgentEngine;

/// <summary>
/// The classified outcome of a control / JSON-RPC response frame, produced purely by a protocol's
/// <c>ClassifyControlResponse</c> (no process/store access): exactly one of
/// <paramref name="Result"/> (the payload to hand the awaiting caller) or <paramref name="Error"/>
/// (the exception to fault it with). The session pump then completes the pending
/// waiter — the lookup and TCS-set live there, not in the protocol.
/// </summary>
public readonly record struct ControlResponseOutcome(JsonElement? Result, Exception? Error);
