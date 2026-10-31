using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine;

/// <summary>
/// One user turn to send to a session. Because the session is DB-free it can't fetch any of this —
/// the caller (the execution-service adapter) supplies it: <paramref name="Content"/> is the message
/// text; <paramref name="ContextBlock"/> is the workspace context block to inline on a first turn
/// (null to skip — e.g. every follow-up); <paramref name="ImagesJson"/> is the serialized image
/// attachments (null for none).
/// </summary>
public sealed record SessionTurn(string Content, string? ContextBlock = null, string? ImagesJson = null);
