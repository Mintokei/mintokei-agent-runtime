using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentEngine.Claude;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Characterization tests for the DB-free <see cref="AgentSession"/> engine, driven by a
/// <see cref="FakeProcessHandle"/> (no real CLI, no WebApplicationFactory — runnable anywhere).
/// They pin the engine's own responsibilities against the Claude protocol: handshake round-trip,
/// one-way transcript → <see cref="IAgentSession.Output"/>, control-response routing, the
/// <see cref="InteractionMode.Surface"/> interaction path + <see cref="IAgentSession.RespondAsync"/>,
/// waiter-failing on stream end, and interrupt. Parser/reply-builder correctness is covered by their
/// own golden tests; here we only assert what the session engine does with what the parser emits.
/// </summary>
public class AgentSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static AgentSession NewSession(FakeProcessHandle fake, AgentSessionOptions? options = null)
    {
        var logger = NullLogger.Instance;
        return new AgentSession(
            sessionId: Guid.NewGuid(),
            handle: fake,
            output: fake.Output,
            protocol: new ClaudeSessionProtocol(logger),
            replyBuilder: new ClaudeInteractionReplyBuilder(),
            options: options ?? new AgentSessionOptions(),
            cts: new CancellationTokenSource(),
            logger: logger);
    }

    /// <summary>Starts the session and completes the <c>initialize</c> handshake by feeding the
    /// matching control_response, returning the ready session.</summary>
    private static async Task<AgentSession> StartHandshakenAsync(FakeProcessHandle fake, AgentSessionOptions? options = null)
    {
        var session = NewSession(fake, options);
        var startTask = session.StartAsync(resume: false, CancellationToken.None);

        var initLine = await fake.WaitForWriteAsync(l => l.Contains("\"subtype\":\"initialize\""), Timeout);
        fake.FeedStdout(ControlResponse(RequestIdOf(initLine), "success"));

        await startTask.WaitAsync(Timeout);
        return session;
    }

    private static string RequestIdOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("request_id").GetString()!;
    }

    private static string ControlResponse(string requestId, string subtype)
        => JsonSerializer.Serialize(new { type = "control_response", response = new { request_id = requestId, subtype } });

    private static async Task<AgentStreamOutput> NextAsync(IAsyncEnumerator<AgentStreamOutput> outputs)
    {
        var moved = await outputs.MoveNextAsync().AsTask().WaitAsync(Timeout);
        Assert.True(moved, "expected another output item, but the stream completed");
        return outputs.Current;
    }

    [Fact]
    public async Task StartAsync_sends_initialize_and_completes_when_the_control_response_routes()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenAsync(fake);

        // Reaching here means the handshake round-tripped through the pump.
        Assert.Contains(fake.Writes, w => w.Contains("\"type\":\"control_request\"") && w.Contains("\"initialize\""));
        Assert.False(fake.HasExited);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Pump_publishes_one_way_output_and_tracks_the_reported_session_id()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenAsync(fake);
        await using var outputs = session.Output.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        fake.FeedStdout("""{"type":"system","session_id":"sess-123"}""");

        var item = await NextAsync(outputs);
        var changed = Assert.IsType<SessionIdChanged>(item);
        Assert.Equal("sess-123", changed.SessionId);
        Assert.Equal("sess-123", session.AgentSessionId);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Interaction_is_surfaced_on_Output_and_RespondAsync_writes_the_reply()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenAsync(fake);
        await using var outputs = session.Output.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        fake.FeedStdout(
            """{"type":"control_request","request_id":"perm-1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls"}}}""");

        var q = Assert.IsType<InteractionRequested>(await NextAsync(outputs));
        Assert.Equal("perm-1", q.RequestId);

        var answered = await session.RespondAsync("perm-1", new UserInteractionResponse("allow", null, null), TestContext.Current.CancellationToken);
        Assert.True(answered);

        var reply = await fake.WaitForWriteAsync(
            l => l.Contains("\"type\":\"control_response\"") && l.Contains("perm-1"), Timeout);
        Assert.Contains("\"behavior\":\"allow\"", reply);

        // Answering the same request id again is a no-op — it was consumed.
        Assert.False(await session.RespondAsync("perm-1", new UserInteractionResponse("allow", null, null), TestContext.Current.CancellationToken));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Handshake_faults_with_recent_stderr_when_the_stream_ends_before_the_response()
    {
        var fake = new FakeProcessHandle();
        var session = NewSession(fake);

        var startTask = session.StartAsync(resume: false, CancellationToken.None);
        // Ensure the request was written (so the waiter is registered) before ending the stream.
        await fake.WaitForWriteAsync(l => l.Contains("\"subtype\":\"initialize\""), Timeout);
        fake.FeedStderr("claude: authentication_error (invalid API key)");
        fake.CompleteOutput();

        // Fails the pending waiter with the CLI's own error, not a bare "stream ended".
        var ex = await Assert.ThrowsAsync<AgentStreamEndedException>(async () => await startTask.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Contains("Output stream ended", ex.Message);
        Assert.Contains("authentication_error", ex.Message);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task InterruptAsync_signals_the_cli_then_reports_false_after_exit()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenAsync(fake);

        Assert.True(await session.InterruptAsync(TestContext.Current.CancellationToken));
        await fake.WaitForWriteAsync(l => l.Contains("\"subtype\":\"interrupt\""), Timeout);

        fake.Kill();
        Assert.False(await session.InterruptAsync(TestContext.Current.CancellationToken));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task SendTurnAsync_sends_plain_content_and_inlines_a_context_block()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenAsync(fake);

        // Plain follow-up turn: no context block.
        await session.SendMessageAsync("hello world", TestContext.Current.CancellationToken);
        var plain = await fake.WaitForWriteAsync(
            l => l.Contains("\"type\":\"user\"") && l.Contains("hello world"), Timeout);
        Assert.DoesNotContain("WS_CONTEXT_BLOCK", plain);

        // First turn with a context block: inlined into the same stream-json user message.
        await session.SendTurnAsync(new SessionTurn("second message", ContextBlock: "WS_CONTEXT_BLOCK"), TestContext.Current.CancellationToken);
        var withContext = await fake.WaitForWriteAsync(
            l => l.Contains("\"type\":\"user\"") && l.Contains("second message"), Timeout);
        Assert.Contains("WS_CONTEXT_BLOCK", withContext);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task CompactAsync_sends_a_slash_compact_user_message()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenAsync(fake);

        await session.CompactAsync("focus on the API", TestContext.Current.CancellationToken);

        var msg = await fake.WaitForWriteAsync(
            l => l.Contains("\"type\":\"user\"") && l.Contains("/compact"), Timeout);
        Assert.Contains("focus on the API", msg);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ApplyConfigAsync_round_trips_a_control_request_for_a_changed_model()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenAsync(fake);

        var oldConfig = new Dictionary<string, string?> { ["model"] = "claude-old" };
        var newConfig = new Dictionary<string, string?> { ["model"] = "claude-new" };
        var applyTask = session.ApplyConfigAsync(oldConfig, newConfig, TestContext.Current.CancellationToken);

        // set_model goes out as a control_request/response round-trip — complete it.
        var req = await fake.WaitForWriteAsync(
            l => l.Contains("\"type\":\"control_request\"") && l.Contains("set_model"), Timeout);
        Assert.Contains("claude-new", req);
        fake.FeedStdout(ControlResponse(RequestIdOf(req), "success"));

        Assert.True(await applyTask.WaitAsync(Timeout, TestContext.Current.CancellationToken));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task AutoApprove_allows_claude_permissions_without_surfacing()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenAsync(
            fake, new AgentSessionOptions { InteractionMode = InteractionMode.AutoApprove });
        await using var outputs = session.Output.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        fake.FeedStdout(
            """{"type":"control_request","request_id":"perm-1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls"}}}""");
        // Sentinel one-way frame right after the permission: if the interaction were surfaced it would
        // reach Output first; auto-approve must consume it inline, so the sentinel is the first item.
        fake.FeedStdout("""{"type":"system","session_id":"sess-xyz"}""");

        // "allow" (not "accept") — the per-backend AcceptDecision, so Claude's builder emits behavior:allow.
        var reply = await fake.WaitForWriteAsync(
            l => l.Contains("\"type\":\"control_response\"") && l.Contains("perm-1"), Timeout);
        Assert.Contains("\"behavior\":\"allow\"", reply);

        Assert.IsType<SessionIdChanged>(await NextAsync(outputs));

        await session.DisposeAsync();
    }
}
