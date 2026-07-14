using System.Text.Json;
using Mintokei.AgentEngine.AgentTools.Acp;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Tests for AcpJsonRpcHelper — the static JSON-RPC parsing + extraction surface the
/// execution service + model discovery rely on. Simple pure-function cases; locks in
/// the shapes we read out of ACP responses (Copilot, OpenCode, …).
/// </summary>
public class AcpJsonRpcHelperTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── TryParseJsonRpc ──

    [Fact]
    public void TryParseJsonRpc_Valid_Response_ReturnsTrue()
    {
        Assert.True(AcpJsonRpcHelper.TryParseJsonRpc("""{"jsonrpc":"2.0","id":1,"result":{}}""", out var el));
        Assert.Equal(JsonValueKind.Object, el.ValueKind);
    }

    [Fact]
    public void TryParseJsonRpc_Valid_Notification_ReturnsTrue()
    {
        // Notifications have no id but have a method.
        Assert.True(AcpJsonRpcHelper.TryParseJsonRpc("""{"jsonrpc":"2.0","method":"session/update","params":{}}""", out var el));
        Assert.True(el.TryGetProperty("method", out _));
    }

    [Fact]
    public void TryParseJsonRpc_Invalid_Json_ReturnsFalse()
    {
        Assert.False(AcpJsonRpcHelper.TryParseJsonRpc("not json", out _));
    }

    [Fact]
    public void TryParseJsonRpc_PlainObject_NoJsonRpcFields_ReturnsFalse()
    {
        // Must have at least one of: jsonrpc, id, method.
        Assert.False(AcpJsonRpcHelper.TryParseJsonRpc("""{"foo":"bar"}""", out _));
    }

    // ── ExtractSessionId ──

    [Fact]
    public void ExtractSessionId_FromSessionNewResponse_ReturnsId()
    {
        var response = Parse("""{"jsonrpc":"2.0","id":2,"result":{"sessionId":"abc-123","models":{"availableModels":[]}}}""");
        Assert.Equal("abc-123", AcpJsonRpcHelper.ExtractSessionId(response));
    }

    [Fact]
    public void ExtractSessionId_MissingField_ReturnsNull()
    {
        var response = Parse("""{"jsonrpc":"2.0","id":2,"result":{}}""");
        Assert.Null(AcpJsonRpcHelper.ExtractSessionId(response));
    }

    [Fact]
    public void ExtractSessionId_NoResult_ReturnsNull()
    {
        var response = Parse("""{"jsonrpc":"2.0","id":2,"error":{"code":-1,"message":"no"}}""");
        Assert.Null(AcpJsonRpcHelper.ExtractSessionId(response));
    }

    // ── ExtractStopReason ──

    [Theory]
    [InlineData("end_turn")]
    [InlineData("cancelled")]
    [InlineData("max_tokens")]
    [InlineData("max_turn_requests")]
    [InlineData("refusal")]
    public void ExtractStopReason_Valid_ReturnsReason(string reason)
    {
        var json = "{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"stopReason\":\"" + reason + "\"}}";
        var response = Parse(json);
        Assert.Equal(reason, AcpJsonRpcHelper.ExtractStopReason(response));
    }

    [Fact]
    public void ExtractStopReason_MissingField_ReturnsNull()
    {
        var response = Parse("""{"jsonrpc":"2.0","id":3,"result":{}}""");
        Assert.Null(AcpJsonRpcHelper.ExtractStopReason(response));
    }
}
