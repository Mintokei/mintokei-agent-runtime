using System.Net.WebSockets;
using Mintokei.Runner.Contracts.Tunnel;

namespace Mintokei.Runner;

/// <summary>
/// Handles a single HTTP request received through the tunnel by making a local HTTP call.
/// </summary>
public static class TunnelRequestHandler
{
    private const int MaxResponseBodySize = 10 * 1024 * 1024; // 10 MB
    private const int StreamChunkSize = 16 * 1024;            // 16 KB chunks for streaming

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailers", "Transfer-Encoding", "Upgrade",
    };

    /// <summary>
    /// Make the local HTTP request and forward the response back through the tunnel.
    /// For streaming content types (e.g. text/event-stream) the response is forwarded
    /// chunk-by-chunk via HttpResponseStart/Chunk/End frames. Otherwise a single
    /// HttpResponse frame is sent.
    /// </summary>
    public static async Task HandleAsync(
        HttpClient httpClient,
        ClientWebSocket ws,
        SemaphoreSlim writeLock,
        Guid requestId,
        TunnelHttpRequest tunnelRequest,
        ReadOnlyMemory<byte> requestBody,
        CancellationToken ct)
    {
        try
        {
            var url = $"http://localhost:{tunnelRequest.Port}{tunnelRequest.Path}{tunnelRequest.QueryString}";
            var method = new HttpMethod(tunnelRequest.Method);
            using var request = new HttpRequestMessage(method, url);

            // Copy headers, filtering hop-by-hop
            foreach (var (key, value) in tunnelRequest.Headers)
            {
                if (HopByHopHeaders.Contains(key))
                    continue;
                if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Try as request header first, then as content header
                if (!request.Headers.TryAddWithoutValidation(key, value))
                    request.Content?.Headers.TryAddWithoutValidation(key, value);
            }

            // Set body for methods that support it
            if (requestBody.Length > 0)
            {
                request.Content = new ByteArrayContent(requestBody.ToArray());
                if (tunnelRequest.Headers.TryGetValue("Content-Type", out var contentType))
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            // Use ResponseHeadersRead so we can detect SSE before buffering the body
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            // Build response headers
            var responseHeaders = new Dictionary<string, string>();
            foreach (var header in response.Headers)
            {
                if (HopByHopHeaders.Contains(header.Key))
                    continue;
                responseHeaders[header.Key] = string.Join(", ", header.Value);
            }
            foreach (var header in response.Content.Headers)
            {
                if (HopByHopHeaders.Contains(header.Key))
                    continue;
                responseHeaders[header.Key] = string.Join(", ", header.Value);
            }

            var contentTypeValue = response.Content.Headers.ContentType?.MediaType ?? "";
            // Stream when:
            //   (a) the content type is SSE (long-lived, push-based), or
            //   (b) the body is too large to safely buffer in memory, or
            //   (c) the body length is unknown (chunked encoding) — could be huge.
            // Range responses for video/audio (<10 MB slices) still take the
            // single-frame path, which is the common case.
            var declaredLength = response.Content.Headers.ContentLength;
            var isStreaming = contentTypeValue.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase)
                || declaredLength is null
                || declaredLength > MaxResponseBodySize;

            var tunnelResponse = new TunnelHttpResponse((int)response.StatusCode, responseHeaders);

            if (isStreaming)
            {
                // Send the start frame, then stream chunks until the local server closes the connection.
                var startFrame = TunnelFrameCodec.EncodeResponseStart(requestId, tunnelResponse);
                await SendFrameAsync(ws, writeLock, startFrame, ct);

                var bodyStream = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[StreamChunkSize];
                try
                {
                    int read;
                    while ((read = await bodyStream.ReadAsync(buffer, ct)) > 0)
                    {
                        var chunk = new byte[read];
                        Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                        var chunkFrame = TunnelFrameCodec.EncodeResponseChunk(requestId, chunk);
                        await SendFrameAsync(ws, writeLock, chunkFrame, ct);
                    }
                }
                finally
                {
                    var endFrame = TunnelFrameCodec.EncodeResponseEnd(requestId);
                    try { await SendFrameAsync(ws, writeLock, endFrame, ct); }
                    catch { /* connection may already be gone */ }
                }
                return;
            }

            // Non-streaming: buffer the full body and send a single HttpResponse frame.
            var fullBodyStream = await response.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var readBuffer = new byte[64 * 1024];
            int n;
            while ((n = await fullBodyStream.ReadAsync(readBuffer, ct)) > 0)
            {
                ms.Write(readBuffer, 0, n);
                if (ms.Length > MaxResponseBodySize)
                {
                    var errFrame = TunnelFrameCodec.EncodeError(requestId,
                        new TunnelErrorResponse($"Response body exceeded {MaxResponseBodySize / 1024 / 1024} MB limit."));
                    await SendFrameAsync(ws, writeLock, errFrame, ct);
                    return;
                }
            }

            var responseFrame = TunnelFrameCodec.EncodeResponse(requestId, tunnelResponse, ms.ToArray());
            await SendFrameAsync(ws, writeLock, responseFrame, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
        {
            var errFrame = TunnelFrameCodec.EncodeError(requestId,
                new TunnelErrorResponse($"Connection refused on port {tunnelRequest.Port}"));
            try { await SendFrameAsync(ws, writeLock, errFrame, ct); } catch { }
        }
        catch (TaskCanceledException)
        {
            var errFrame = TunnelFrameCodec.EncodeError(requestId,
                new TunnelErrorResponse("Request to local server timed out."));
            try { await SendFrameAsync(ws, writeLock, errFrame, ct); } catch { }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var errFrame = TunnelFrameCodec.EncodeError(requestId,
                new TunnelErrorResponse($"Error proxying request: {ex.Message}"));
            try { await SendFrameAsync(ws, writeLock, errFrame, ct); } catch { }
        }
    }

    private static async Task SendFrameAsync(ClientWebSocket ws, SemaphoreSlim writeLock, byte[] frame, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
