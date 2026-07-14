using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentEngine.Codex;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Characterization tests for <see cref="CodexSessionProtocol"/> on the shared <see cref="AgentSession"/>
/// engine, driven by a <see cref="FakeProcessHandle"/>. They exercise the JSON-RPC specifics that differ
/// from Claude: the integer-id round-trip (session id is a numeric string parsed to an int on the wire),
/// the initialize → initialized → thread/start handshake with threadId discovery, turn/start + turnId
/// tracking, and turn/interrupt params.
/// </summary>
public class CodexSessionProtocolTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static AgentSession NewCodexSession(FakeProcessHandle fake)
    {
        var logger = NullLogger.Instance;
        return new AgentSession(
            sessionId: Guid.NewGuid(),
            handle: fake,
            output: fake.Output,
            protocol: new CodexSessionProtocol(logger, CodexConfigMapper.Map(new Dictionary<string, string?>()), systemPrompt: null),
            replyBuilder: new CodexInteractionReplyBuilder(),
            options: new AgentSessionOptions(),
            cts: new CancellationTokenSource(),
            logger: logger);
    }

    private static int JsonRpcIdOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("id").GetInt32(); // throws if the id isn't an integer — that's the point
    }

    private static string JsonRpcResult(int id, object result)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });

    private static async Task<AgentSession> StartHandshakenCodexAsync(FakeProcessHandle fake, string threadId)
    {
        var session = NewCodexSession(fake);
        var startTask = session.StartAsync(resume: false, CancellationToken.None);

        var initId = JsonRpcIdOf(await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"initialize\""), Timeout));
        fake.FeedStdout(JsonRpcResult(initId, new { }));

        var startId = JsonRpcIdOf(await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"thread/start\""), Timeout));
        fake.FeedStdout(JsonRpcResult(startId, new { thread = new { id = threadId } }));

        await startTask.WaitAsync(Timeout);
        return session;
    }

    [Fact]
    public async Task Handshake_initializes_then_starts_a_thread_and_reports_the_thread_id()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenCodexAsync(fake, "thread-42");

        Assert.Equal("thread-42", session.AgentSessionId);
        // The initialized notification (no id) went out between initialize and thread/start.
        Assert.Contains(fake.Writes, w => w.Contains("\"method\":\"initialized\"") && !w.Contains("\"id\""));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task SendTurn_starts_a_turn_on_the_thread_and_tracks_the_turn_id()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenCodexAsync(fake, "thread-1");

        var sendTask = session.SendMessageAsync("hello there");
        var turnReq = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"turn/start\""), Timeout);
        Assert.Contains("thread-1", turnReq);
        Assert.Contains("hello there", turnReq);

        fake.FeedStdout(JsonRpcResult(JsonRpcIdOf(turnReq), new { turn = new { id = "turn-9" } }));
        await sendTask.WaitAsync(Timeout);

        Assert.Equal("turn-9", session.CurrentTurnId);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task RollbackAsync_round_trips_thread_rollback_with_the_thread_id_and_turn_count()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenCodexAsync(fake, "thread-1");

        var rollbackTask = session.RollbackAsync(3);
        var rollbackReq = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"thread/rollback\""), Timeout);
        Assert.Contains("\"threadId\":\"thread-1\"", rollbackReq);
        Assert.Contains("\"numTurns\":3", rollbackReq);

        // Awaited like any round-trip: completes only once the pump routes the response.
        Assert.False(rollbackTask.IsCompleted);
        fake.FeedStdout(JsonRpcResult(JsonRpcIdOf(rollbackReq), new { }));
        await rollbackTask.WaitAsync(Timeout);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task InterruptAsync_sends_turn_interrupt_with_the_thread_and_turn_ids()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenCodexAsync(fake, "thread-1");

        var sendTask = session.SendMessageAsync("work");
        var turnReq = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"turn/start\""), Timeout);
        fake.FeedStdout(JsonRpcResult(JsonRpcIdOf(turnReq), new { turn = new { id = "turn-7" } }));
        await sendTask.WaitAsync(Timeout);

        Assert.True(await session.InterruptAsync());
        var interrupt = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"turn/interrupt\""), Timeout);
        Assert.Contains("thread-1", interrupt);
        Assert.Contains("turn-7", interrupt);

        await session.DisposeAsync();
    }
}
