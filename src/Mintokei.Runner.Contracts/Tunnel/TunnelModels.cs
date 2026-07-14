namespace Mintokei.Runner.Contracts.Tunnel;

/// <summary>
/// Metadata for an HTTP request forwarded through the tunnel (API → Runner).
/// </summary>
public sealed record TunnelHttpRequest(
    string Method,
    string Path,
    string? QueryString,
    int Port,
    Dictionary<string, string> Headers);

/// <summary>
/// Metadata for an HTTP response returned through the tunnel (Runner → API).
/// </summary>
public sealed record TunnelHttpResponse(
    int StatusCode,
    Dictionary<string, string> Headers);

/// <summary>
/// Error response when the runner cannot fulfill the request.
/// </summary>
public sealed record TunnelErrorResponse(
    string Message);

/// <summary>
/// Metadata for a WebSocket open request forwarded through the tunnel (API → Runner).
/// </summary>
public sealed record TunnelWsOpenRequest(
    string Path,
    string? QueryString,
    int Port,
    string? SubProtocol,
    Dictionary<string, string> Headers);

/// <summary>
/// Confirmation that the runner opened a local WebSocket (Runner → API).
/// </summary>
public sealed record TunnelWsOpenedResponse(
    string? NegotiatedSubProtocol);

/// <summary>
/// Header for a relayed WebSocket data frame (bidirectional).
/// </summary>
public sealed record TunnelWsDataHeader(
    bool IsText);

/// <summary>
/// Header for a WebSocket close notification (bidirectional).
/// </summary>
public sealed record TunnelWsCloseHeader(
    int StatusCode,
    string? Reason);
