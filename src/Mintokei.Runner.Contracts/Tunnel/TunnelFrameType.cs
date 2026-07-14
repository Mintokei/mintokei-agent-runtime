namespace Mintokei.Runner.Contracts.Tunnel;

/// <summary>
/// Frame type byte for the tunnel binary protocol.
/// </summary>
public enum TunnelFrameType : byte
{
    HttpRequest = 0x01,
    HttpResponse = 0x02,
    Error = 0x03,
    Ping = 0x04,
    Pong = 0x05,
    WsOpen = 0x06,
    WsOpened = 0x07,
    WsData = 0x08,
    WsClose = 0x09,

    // Streaming HTTP response (for SSE and other long-lived responses).
    // Sent by the runner instead of HttpResponse when the local server returns
    // a streaming Content-Type. The API forwards chunks to the browser as they
    // arrive instead of buffering the full response.
    HttpResponseStart = 0x0A,  // status + headers
    HttpResponseChunk = 0x0B,  // body chunk (empty header, raw bytes)
    HttpResponseEnd = 0x0C,    // end-of-stream marker
}
