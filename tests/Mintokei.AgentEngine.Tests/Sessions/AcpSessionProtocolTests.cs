using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentEngine.Acp;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Characterization tests for <see cref="AcpSessionProtocol"/> on the shared <see cref="AgentSession"/>
/// engine, driven by a <see cref="FakeProcessHandle"/>. ACP is the intricate one: turn completion is the
/// <c>session/prompt</c> RESPONSE (a second producer into Output, off the pump), and <c>session/load</c>
/// replays history that must be gated off. These pin: the initialize → session/new handshake, the
/// prompt → TurnEnded round-trip with stopReason→failure mapping, session/cancel, and the replay gate.
/// </summary>
public class AcpSessionProtocolTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static AgentSession NewAcpSession(FakeProcessHandle fake, string? initialSessionId = null)
    {
        var logger = NullLogger.Instance;
        return new AgentSession(
            sessionId: Guid.NewGuid(),
            handle: fake,
            output: fake.Output,
            protocol: new AcpSessionProtocol(logger, cwd: "/tmp/work", mcpServers: Array.Empty<object>()),
            replyBuilder: new AcpInteractionReplyBuilder(),
            options: new AgentSessionOptions(),
            cts: new CancellationTokenSource(),
            logger: logger,
            initialAgentSessionId: initialSessionId);
    }

    private static int JsonRpcIdOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    private static string JsonRpcResult(int id, object result)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });

    private static async Task<AgentStreamOutput> NextAsync(IAsyncEnumerator<AgentStreamOutput> outputs)
    {
        var moved = await outputs.MoveNextAsync().AsTask().WaitAsync(Timeout);
        Assert.True(moved, "expected another output item, but the stream completed");
        return outputs.Current;
    }

    private static async Task<TurnEnded> ReadTurnEndedAsync(IAsyncEnumerator<AgentStreamOutput> outputs)
    {
        for (var i = 0; i < 20; i++)
            if (await NextAsync(outputs) is TurnEnded te)
                return te;
        throw new Xunit.Sdk.XunitException("No TurnEnded within 20 outputs");
    }

    private static async Task<AgentSession> StartHandshakenAcpAsync(FakeProcessHandle fake, string sessionId)
    {
        var session = NewAcpSession(fake);
        var startTask = session.StartAsync(resume: false, CancellationToken.None);

        fake.FeedStdout(JsonRpcResult(
            JsonRpcIdOf(await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"initialize\""), Timeout)), new { }));
        fake.FeedStdout(JsonRpcResult(
            JsonRpcIdOf(await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"session/new\""), Timeout)), new { sessionId }));

        await startTask.WaitAsync(Timeout);
        return session;
    }

    [Fact]
    public async Task Handshake_initializes_then_creates_a_session_and_reports_the_id()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenAcpAsync(fake, "sess-42");

        Assert.Equal("sess-42", session.AgentSessionId);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task SendTurn_prompts_then_ends_the_turn_when_session_prompt_responds()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenAcpAsync(fake, "sess-1");
        await using var outputs = session.Output.GetAsyncEnumerator();

        await session.SendMessageAsync("do the thing");
        var promptReq = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"session/prompt\""), Timeout);
        Assert.Contains("sess-1", promptReq);
        Assert.Contains("do the thing", promptReq);

        // The response IS the turn completion.
        fake.FeedStdout(JsonRpcResult(JsonRpcIdOf(promptReq), new { stopReason = "end_turn" }));

        var turnEnded = await ReadTurnEndedAsync(outputs);
        Assert.False(turnEnded.IsInterrupted);
        Assert.Null(turnEnded.Failure);
        Assert.Null(session.CurrentTurnId);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task SendTurn_maps_a_refusal_stopReason_to_a_turn_failure()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenAcpAsync(fake, "sess-1");
        await using var outputs = session.Output.GetAsyncEnumerator();

        await session.SendMessageAsync("please");
        var promptReq = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"session/prompt\""), Timeout);
        fake.FeedStdout(JsonRpcResult(JsonRpcIdOf(promptReq), new { stopReason = "refusal" }));

        var turnEnded = await ReadTurnEndedAsync(outputs);
        Assert.False(turnEnded.IsInterrupted);
        Assert.NotNull(turnEnded.Failure);
        Assert.Equal(TurnFailureKind.Refusal, turnEnded.Failure!.Kind);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task InterruptAsync_sends_session_cancel_for_the_session()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenAcpAsync(fake, "sess-1");

        await session.SendMessageAsync("work");
        await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"session/prompt\""), Timeout);

        Assert.True(await session.InterruptAsync());
        var cancel = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"session/cancel\""), Timeout);
        Assert.Contains("sess-1", cancel);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Replay_gate_suppresses_history_replayed_during_session_load()
    {
        var fake = new FakeProcessHandle();
        var session = NewAcpSession(fake, initialSessionId: "sess-1");
        var startTask = session.StartAsync(resume: true, CancellationToken.None);

        fake.FeedStdout(JsonRpcResult(
            JsonRpcIdOf(await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"initialize\""), Timeout)), new { }));

        // While session/load is in flight, a replayed agent_message_chunk (would normally emit deltas)
        // must be suppressed by the replay gate.
        var loadReq = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"session/load\""), Timeout);
        fake.FeedStdout(
            """{"jsonrpc":"2.0","method":"session/update","params":{"update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"REPLAYED"}}}}""");
        fake.FeedStdout(JsonRpcResult(JsonRpcIdOf(loadReq), new { }));

        await startTask.WaitAsync(Timeout);

        // The gate held: the only thing on Output is the SessionIdChanged emitted after the load,
        // not a DeltaOutput from the replayed chunk.
        await using var outputs = session.Output.GetAsyncEnumerator();
        Assert.IsType<SessionIdChanged>(await NextAsync(outputs));

        await session.DisposeAsync();
    }
}
