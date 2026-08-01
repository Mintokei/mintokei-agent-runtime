using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// A CLI retries a provider error by itself before giving up, so a caller watching only
/// <see cref="TurnEnded"/> hears nothing until the retry budget is spent — for Claude Code, ten
/// attempts honouring <c>retry-after</c>. These cover the signal that makes reacting sooner possible.
/// </summary>
public class ApiRetryingTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static List<AgentStreamOutput> ParseClaude(string json)
    {
        var parser = new ClaudeStreamParser(NullLogger.Instance, Guid.NewGuid());
        return parser.Consume(Parse(json), isInterrupted: false).ToList();
    }

    private static List<AgentStreamOutput> ParseCodex(string json)
    {
        var parser = new CodexStreamParser(NullLogger.Instance, Guid.NewGuid());
        return parser.Consume(Parse(json)).ToList();
    }

    // ── Claude ────────────────────────────────────────────────────────────

    [Fact]
    public void Claude_reports_a_rate_limit_on_the_first_retry_not_the_last()
    {
        // Real frame: emitted on attempt 1 of 10, ~37s before the CLI even tries again.
        var outputs = ParseClaude("""
            {"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,
             "retry_delay_ms":37000,"error_status":429,"error":"rate_limit",
             "session_id":"s","uuid":"u"}
            """);

        var retry = Assert.Single(outputs.OfType<ApiRetrying>());
        Assert.Equal(TurnFailureKind.RateLimited, retry.Kind);
        Assert.Equal(429, retry.HttpStatus);
        Assert.Equal(1, retry.Attempt);
        Assert.Equal(10, retry.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(37), retry.RetryAfter);
    }

    [Fact]
    public void Claude_retry_does_not_end_the_turn()
    {
        // The CLI may still succeed. Reporting a failure here would abandon a turn that recovers.
        var outputs = ParseClaude("""
            {"type":"system","subtype":"api_retry","attempt":2,"max_retries":10,
             "error_status":529,"error":"overloaded","session_id":"s"}
            """);

        Assert.Empty(outputs.OfType<TurnEnded>());
        Assert.Equal(TurnFailureKind.Overloaded, Assert.Single(outputs.OfType<ApiRetrying>()).Kind);
    }

    [Theory]
    [InlineData(429, TurnFailureKind.RateLimited)]
    [InlineData(529, TurnFailureKind.Overloaded)]
    [InlineData(401, TurnFailureKind.Auth)]
    [InlineData(503, TurnFailureKind.ApiError)]
    public void Claude_classifies_the_status_so_callers_need_not_parse_provider_wording(
        int status, TurnFailureKind expected)
    {
        var outputs = ParseClaude($$"""
            {"type":"system","subtype":"api_retry","attempt":1,"error_status":{{status}},"session_id":"s"}
            """);

        Assert.Equal(expected, Assert.Single(outputs.OfType<ApiRetrying>()).Kind);
    }

    [Fact]
    public void Claude_falls_back_to_the_error_slug_when_no_status_is_reported()
    {
        var outputs = ParseClaude("""
            {"type":"system","subtype":"api_retry","attempt":1,"error":"rate_limit","session_id":"s"}
            """);

        var retry = Assert.Single(outputs.OfType<ApiRetrying>());
        Assert.Equal(TurnFailureKind.RateLimited, retry.Kind);
        Assert.Null(retry.HttpStatus);
    }

    [Fact]
    public void An_unclassifiable_retry_is_still_reported_rather_than_dropped()
    {
        // Knowing "something upstream is failing and it is retrying" beats knowing nothing.
        var outputs = ParseClaude("""
            {"type":"system","subtype":"api_retry","attempt":1,"session_id":"s"}
            """);

        Assert.Equal(TurnFailureKind.ApiError, Assert.Single(outputs.OfType<ApiRetrying>()).Kind);
    }

    [Fact]
    public void A_system_frame_that_is_not_a_retry_is_unaffected()
    {
        var outputs = ParseClaude("""{"type":"system","subtype":"init","session_id":"abc"}""");

        Assert.Empty(outputs.OfType<ApiRetrying>());
        Assert.Equal("abc", Assert.Single(outputs.OfType<SessionIdChanged>()).SessionId);
    }

    // ── Codex ─────────────────────────────────────────────────────────────

    [Fact]
    public void Codex_surfaces_a_retrying_error_instead_of_swallowing_it()
    {
        var outputs = ParseCodex("""
            {"method":"error","params":{"threadId":"t","turnId":"u","willRetry":true,
             "error":{"message":"429 Too Many Requests"}}}
            """);

        var retry = Assert.Single(outputs.OfType<ApiRetrying>());
        Assert.Equal(TurnFailureKind.RateLimited, retry.Kind);
        Assert.Contains("429", retry.Message);
    }

    [Fact]
    public void A_codex_error_that_will_retry_is_not_a_turn_failure()
    {
        var outputs = ParseCodex("""
            {"method":"error","params":{"threadId":"t","turnId":"u","willRetry":true,
             "error":{"message":"stream disconnected"}}}
            """);

        // No error message row, and nothing that would make the following turn/completed a failure.
        Assert.Empty(outputs.OfType<MessageOutput>());
        Assert.Single(outputs.OfType<ApiRetrying>());
    }

    [Fact]
    public void A_codex_error_that_will_not_retry_still_fails_the_turn()
    {
        var outputs = ParseCodex("""
            {"method":"error","params":{"threadId":"t","turnId":"u","willRetry":false,
             "error":{"message":"exceeded retry limit, last status: 429 Too Many Requests"}}}
            """);

        Assert.Empty(outputs.OfType<ApiRetrying>());
        var message = Assert.Single(outputs.OfType<MessageOutput>());
        Assert.Equal(MessageType.Error, message.Message.Type);
    }
}
