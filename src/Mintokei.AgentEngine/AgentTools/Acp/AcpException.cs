using System.Text.Json;

namespace Mintokei.AgentEngine.AgentTools.Acp;

/// <summary>
/// Thrown when an ACP JSON-RPC response contains an error instead of a result.
/// </summary>
public sealed class AcpException : InvalidOperationException
{
    public int ErrorCode { get; }
    public string ErrorMessage { get; }
    public JsonElement RawResponse { get; }

    public AcpException(string message, int errorCode, string errorMessage, JsonElement rawResponse)
        : base(message)
    {
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        RawResponse = rawResponse;
    }
}
