using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Mintokei.Runner.Contracts.Tunnel;

/// <summary>
/// Encodes and decodes binary tunnel frames.
///
/// Frame layout:
///   [1 byte]    FrameType
///   [16 bytes]  RequestId (GUID, little-endian)
///   [4 bytes]   HeaderLength (uint32, little-endian) — length of the metadata JSON
///   [N bytes]   HeaderJson (UTF-8 JSON)
///   [remaining] Body (raw bytes)
/// </summary>
public static class TunnelFrameCodec
{
    public const int FixedHeaderSize = 1 + 16 + 4; // 21 bytes

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Encode a request frame (API → Runner).
    /// </summary>
    public static byte[] EncodeRequest(Guid requestId, TunnelHttpRequest request, ReadOnlySpan<byte> body)
    {
        return Encode(TunnelFrameType.HttpRequest, requestId, request, body);
    }

    /// <summary>
    /// Encode a response frame (Runner → API).
    /// </summary>
    public static byte[] EncodeResponse(Guid requestId, TunnelHttpResponse response, ReadOnlySpan<byte> body)
    {
        return Encode(TunnelFrameType.HttpResponse, requestId, response, body);
    }

    /// <summary>
    /// Encode an error frame (Runner → API).
    /// </summary>
    public static byte[] EncodeError(Guid requestId, TunnelErrorResponse error)
    {
        return Encode(TunnelFrameType.Error, requestId, error, ReadOnlySpan<byte>.Empty);
    }

    // ── Streaming HTTP response ──────────────────────────────────────

    /// <summary>
    /// Encode the start frame of a streamed response (Runner → API).
    /// Carries the status code and headers; the body arrives in subsequent
    /// HttpResponseChunk frames and ends with HttpResponseEnd.
    /// </summary>
    public static byte[] EncodeResponseStart(Guid requestId, TunnelHttpResponse response)
    {
        return Encode(TunnelFrameType.HttpResponseStart, requestId, response, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Encode a body chunk frame for a streamed response (Runner → API).
    /// </summary>
    public static byte[] EncodeResponseChunk(Guid requestId, ReadOnlySpan<byte> chunk)
    {
        // No metadata header — just raw body bytes.
        var totalLength = FixedHeaderSize + chunk.Length;
        var buffer = new byte[totalLength];
        buffer[0] = (byte)TunnelFrameType.HttpResponseChunk;
        requestId.TryWriteBytes(buffer.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(17, 4), 0);
        chunk.CopyTo(buffer.AsSpan(FixedHeaderSize));
        return buffer;
    }

    /// <summary>
    /// Encode the end-of-stream frame for a streamed response (Runner → API).
    /// </summary>
    public static byte[] EncodeResponseEnd(Guid requestId)
    {
        return EncodeMinimal(TunnelFrameType.HttpResponseEnd, requestId);
    }

    /// <summary>
    /// Decode a streaming response start frame.
    /// </summary>
    public static (Guid RequestId, TunnelHttpResponse Response) DecodeResponseStart(byte[] frame)
    {
        var (frameType, requestId, headerLength) = DecodeHeader(frame);
        if (frameType != TunnelFrameType.HttpResponseStart)
            throw new InvalidDataException($"Expected HttpResponseStart frame, got {frameType}.");

        var headerJson = Encoding.UTF8.GetString(frame, FixedHeaderSize, headerLength);
        var response = JsonSerializer.Deserialize<TunnelHttpResponse>(headerJson, JsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize TunnelHttpResponse.");

        return (requestId, response);
    }

    /// <summary>
    /// Decode a streaming response body chunk frame.
    /// </summary>
    public static (Guid RequestId, ReadOnlyMemory<byte> Body) DecodeResponseChunk(byte[] frame)
    {
        var (frameType, requestId, headerLength) = DecodeHeader(frame);
        if (frameType != TunnelFrameType.HttpResponseChunk)
            throw new InvalidDataException($"Expected HttpResponseChunk frame, got {frameType}.");

        var bodyStart = FixedHeaderSize + headerLength;
        var body = new ReadOnlyMemory<byte>(frame, bodyStart, frame.Length - bodyStart);
        return (requestId, body);
    }

    /// <summary>
    /// Encode a ping frame.
    /// </summary>
    public static byte[] EncodePing(Guid requestId)
    {
        return EncodeMinimal(TunnelFrameType.Ping, requestId);
    }

    /// <summary>
    /// Encode a pong frame.
    /// </summary>
    public static byte[] EncodePong(Guid requestId)
    {
        return EncodeMinimal(TunnelFrameType.Pong, requestId);
    }

    /// <summary>
    /// Decode the fixed header from a frame to determine frame type and request ID
    /// without fully parsing the metadata JSON.
    /// </summary>
    public static (TunnelFrameType FrameType, Guid RequestId, int HeaderLength) DecodeHeader(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < FixedHeaderSize)
            throw new InvalidDataException($"Frame too small: {frame.Length} bytes, expected at least {FixedHeaderSize}.");

        var frameType = (TunnelFrameType)frame[0];
        var requestId = new Guid(frame.Slice(1, 16));
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(17, 4));

        return (frameType, requestId, headerLength);
    }

    /// <summary>
    /// Decode a request frame (API → Runner).
    /// </summary>
    public static (Guid RequestId, TunnelHttpRequest Request, ReadOnlyMemory<byte> Body) DecodeRequest(byte[] frame)
    {
        var (frameType, requestId, headerLength) = DecodeHeader(frame);
        if (frameType != TunnelFrameType.HttpRequest)
            throw new InvalidDataException($"Expected HttpRequest frame, got {frameType}.");

        var headerJson = Encoding.UTF8.GetString(frame, FixedHeaderSize, headerLength);
        var request = JsonSerializer.Deserialize<TunnelHttpRequest>(headerJson, JsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize TunnelHttpRequest.");

        var bodyStart = FixedHeaderSize + headerLength;
        var body = new ReadOnlyMemory<byte>(frame, bodyStart, frame.Length - bodyStart);

        return (requestId, request, body);
    }

    /// <summary>
    /// Decode a response frame (Runner → API).
    /// </summary>
    public static (Guid RequestId, TunnelHttpResponse Response, ReadOnlyMemory<byte> Body) DecodeResponse(byte[] frame)
    {
        var (frameType, requestId, headerLength) = DecodeHeader(frame);
        if (frameType != TunnelFrameType.HttpResponse)
            throw new InvalidDataException($"Expected HttpResponse frame, got {frameType}.");

        var headerJson = Encoding.UTF8.GetString(frame, FixedHeaderSize, headerLength);
        var response = JsonSerializer.Deserialize<TunnelHttpResponse>(headerJson, JsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize TunnelHttpResponse.");

        var bodyStart = FixedHeaderSize + headerLength;
        var body = new ReadOnlyMemory<byte>(frame, bodyStart, frame.Length - bodyStart);

        return (requestId, response, body);
    }

    // ── WebSocket tunnel frames ──────────────────────────────────────

    /// <summary>
    /// Encode a WS open request frame (API → Runner).
    /// </summary>
    public static byte[] EncodeWsOpen(Guid sessionId, TunnelWsOpenRequest request)
        => Encode(TunnelFrameType.WsOpen, sessionId, request, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Encode a WS opened confirmation frame (Runner → API).
    /// </summary>
    public static byte[] EncodeWsOpened(Guid sessionId, TunnelWsOpenedResponse response)
        => Encode(TunnelFrameType.WsOpened, sessionId, response, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Encode a WS data relay frame (bidirectional).
    /// </summary>
    public static byte[] EncodeWsData(Guid sessionId, TunnelWsDataHeader header, ReadOnlySpan<byte> body)
        => Encode(TunnelFrameType.WsData, sessionId, header, body);

    /// <summary>
    /// Encode a WS close notification frame (bidirectional).
    /// </summary>
    public static byte[] EncodeWsClose(Guid sessionId, TunnelWsCloseHeader close)
        => Encode(TunnelFrameType.WsClose, sessionId, close, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Decode a WS open request frame (API → Runner).
    /// </summary>
    public static (Guid SessionId, TunnelWsOpenRequest Request) DecodeWsOpen(byte[] frame)
    {
        var (frameType, sessionId, headerLength) = DecodeHeader(frame);
        if (frameType != TunnelFrameType.WsOpen)
            throw new InvalidDataException($"Expected WsOpen frame, got {frameType}.");

        var headerJson = Encoding.UTF8.GetString(frame, FixedHeaderSize, headerLength);
        var request = JsonSerializer.Deserialize<TunnelWsOpenRequest>(headerJson, JsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize TunnelWsOpenRequest.");

        return (sessionId, request);
    }

    /// <summary>
    /// Decode a WS opened confirmation frame (Runner → API).
    /// </summary>
    public static (Guid SessionId, TunnelWsOpenedResponse Response) DecodeWsOpened(byte[] frame)
    {
        var (frameType, sessionId, headerLength) = DecodeHeader(frame);
        if (frameType != TunnelFrameType.WsOpened)
            throw new InvalidDataException($"Expected WsOpened frame, got {frameType}.");

        var headerJson = Encoding.UTF8.GetString(frame, FixedHeaderSize, headerLength);
        var response = JsonSerializer.Deserialize<TunnelWsOpenedResponse>(headerJson, JsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize TunnelWsOpenedResponse.");

        return (sessionId, response);
    }

    /// <summary>
    /// Decode a WS data relay frame (bidirectional).
    /// </summary>
    public static (Guid SessionId, TunnelWsDataHeader Header, ReadOnlyMemory<byte> Body) DecodeWsData(byte[] frame)
    {
        var (frameType, sessionId, headerLength) = DecodeHeader(frame);
        if (frameType != TunnelFrameType.WsData)
            throw new InvalidDataException($"Expected WsData frame, got {frameType}.");

        var headerJson = Encoding.UTF8.GetString(frame, FixedHeaderSize, headerLength);
        var header = JsonSerializer.Deserialize<TunnelWsDataHeader>(headerJson, JsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize TunnelWsDataHeader.");

        var bodyStart = FixedHeaderSize + headerLength;
        var body = new ReadOnlyMemory<byte>(frame, bodyStart, frame.Length - bodyStart);

        return (sessionId, header, body);
    }

    /// <summary>
    /// Decode a WS close notification frame (bidirectional).
    /// </summary>
    public static (Guid SessionId, TunnelWsCloseHeader Close) DecodeWsClose(byte[] frame)
    {
        var (frameType, sessionId, headerLength) = DecodeHeader(frame);
        if (frameType != TunnelFrameType.WsClose)
            throw new InvalidDataException($"Expected WsClose frame, got {frameType}.");

        var headerJson = Encoding.UTF8.GetString(frame, FixedHeaderSize, headerLength);
        var close = JsonSerializer.Deserialize<TunnelWsCloseHeader>(headerJson, JsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize TunnelWsCloseHeader.");

        return (sessionId, close);
    }

    /// <summary>
    /// Decode an error frame (Runner → API).
    /// </summary>
    public static (Guid RequestId, TunnelErrorResponse Error) DecodeError(byte[] frame)
    {
        var (frameType, requestId, headerLength) = DecodeHeader(frame);
        if (frameType != TunnelFrameType.Error)
            throw new InvalidDataException($"Expected Error frame, got {frameType}.");

        var headerJson = Encoding.UTF8.GetString(frame, FixedHeaderSize, headerLength);
        var error = JsonSerializer.Deserialize<TunnelErrorResponse>(headerJson, JsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize TunnelErrorResponse.");

        return (requestId, error);
    }

    private static byte[] Encode<T>(TunnelFrameType frameType, Guid requestId, T metadata, ReadOnlySpan<byte> body)
    {
        var headerJson = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
        var totalLength = FixedHeaderSize + headerJson.Length + body.Length;
        var buffer = new byte[totalLength];

        buffer[0] = (byte)frameType;
        requestId.TryWriteBytes(buffer.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(17, 4), (uint)headerJson.Length);
        headerJson.CopyTo(buffer.AsSpan(FixedHeaderSize));
        body.CopyTo(buffer.AsSpan(FixedHeaderSize + headerJson.Length));

        return buffer;
    }

    private static byte[] EncodeMinimal(TunnelFrameType frameType, Guid requestId)
    {
        var buffer = new byte[FixedHeaderSize];
        buffer[0] = (byte)frameType;
        requestId.TryWriteBytes(buffer.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(17, 4), 0);
        return buffer;
    }
}
