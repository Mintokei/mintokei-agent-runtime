namespace Mintokei.AgentEngine.AgentTools;

/// <summary>
/// Thrown when an agent process output stream ends unexpectedly during handshake,
/// before the expected protocol response is received.
/// </summary>
public sealed class AgentStreamEndedException : InvalidOperationException
{
    public string? RequestId { get; }

    public AgentStreamEndedException(string message) : base(message) { }

    public AgentStreamEndedException(string message, string requestId) : base(message)
    {
        RequestId = requestId;
    }
}
