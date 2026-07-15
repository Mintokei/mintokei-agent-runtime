using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Tests Stage E adoption: <see cref="IAgentSession.AttachAsync"/> rebuilds a session around an
/// already-initialized process (rehydration after an API restart / runner reconnect) — pump only,
/// NO protocol handshake. The adopted CLI completed its handshake in the previous API instance's
/// lifetime; the session id is seeded from persistence and request ids start high so a stale reply
/// to a pre-restart request can never be routed to a new waiter.
/// </summary>
public class AgentSessionAttachTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static (AgentSession Session, FakeProcessHandle Handle) Adopted(string? persistedSessionId = "sess-abc")
    {
        var handle = new FakeProcessHandle();
        var factory = new AgentSessionFactory(new FakeCommandLineRunnerFactory(), NullLoggerFactory.Instance);
        var session = factory.WrapClaude(
            Guid.NewGuid(), handle, handle.Output, new CancellationTokenSource(), persistedSessionId);
        return (session, handle);
    }

    [Fact]
    public async Task Attach_writes_no_handshake_and_turn_sends_stamp_the_seeded_session_id()
    {
        var (session, handle) = Adopted("sess-abc");
        await using var _ = session;

        await session.AttachAsync(TestContext.Current.CancellationToken);

        // The whole point of adoption: not a single byte toward the CLI — no initialize.
        Assert.Empty(handle.Writes);
        Assert.Equal("sess-abc", session.AgentSessionId);

        // The first post-adopt turn goes out against the persisted session id.
        await session.SendMessageAsync("hello again", TestContext.Current.CancellationToken);
        var write = Assert.Single(handle.Writes);
        Assert.Contains("\"type\":\"user\"", write);
        Assert.Contains("sess-abc", write);
    }

    [Fact]
    public async Task Adopted_request_ids_start_high_and_the_pump_routes_their_responses()
    {
        var (session, handle) = Adopted();
        await using var _ = session;

        await session.AttachAsync(TestContext.Current.CancellationToken);

        // Drive a control round-trip on the adopted session (what set_model / compact would do).
        var roundTrip = session.SendRequestAndWaitAsync("ping", new { }, CancellationToken.None);

        var line = await handle.WaitForWriteAsync(l => l.Contains("\"subtype\":\"ping\""), Timeout);
        using var doc = JsonDocument.Parse(line);
        var requestId = doc.RootElement.GetProperty("request_id").GetString()!;

        // Seeded above AdoptedRequestIdBase: a stale reply to a pre-restart request (whose waiter
        // died with the previous API instance) can never collide with an adopted-session id.
        Assert.True(int.Parse(requestId) > 1_000_000,
            $"adopted request id {requestId} should start above the adoption base");

        // ...and the adopted pump routes the response back to its waiter.
        handle.FeedStdout(JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new { request_id = requestId, subtype = "success" },
        }));
        await roundTrip.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Attached_pump_completes_output_when_the_stream_ends()
    {
        var (session, handle) = Adopted();
        await using var _ = session;

        await session.AttachAsync(TestContext.Current.CancellationToken);
        handle.CompleteOutput();

        // Output completing (rather than hanging) proves the pump actually ran.
        var drained = Task.Run(async () =>
        {
            await foreach (var unused in session.Output) { }
        }, TestContext.Current.CancellationToken);
        await drained.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }
}
