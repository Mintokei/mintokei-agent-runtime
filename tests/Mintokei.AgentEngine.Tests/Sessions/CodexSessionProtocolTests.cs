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
    public async Task Captured_token_usage_notification_reaches_the_session_output()
    {
        // Captured from the production runner uplink; only thread/turn ids are anonymized.
        // Feed the real envelope through the session pump, including JSON-RPC parsing
        // and method dispatch, rather than constructing a notification name in the test.
        var fake = new FakeProcessHandle();
        await using var session = NewCodexSession(fake);
        await session.AttachAsync(TestContext.Current.CancellationToken);
        fake.FeedStdout(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Infrastructure", "Fixtures", "codex-token-usage-live.json"),
            TestContext.Current.CancellationToken));
        fake.FeedStdout("""{"method":"turn/completed","params":{"turn":{"status":"completed"}}}""");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeout);
        var usages = new List<ContextUsagePayload>();
        await foreach (var output in session.Output.WithCancellation(timeout.Token))
        {
            if (output is DeltaOutput { Payload: ContextUsagePayload usage })
                usages.Add(usage);
            if (output is TurnEnded) break;
        }

        var snapshot = Assert.Single(usages);
        Assert.Equal(201165, snapshot.UsedTokens);
        Assert.Equal(475000, snapshot.WindowTokens);
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

        var sendTask = session.SendMessageAsync("hello there", TestContext.Current.CancellationToken);
        var turnReq = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"turn/start\""), Timeout);
        Assert.Contains("thread-1", turnReq);
        Assert.Contains("hello there", turnReq);

        fake.FeedStdout(JsonRpcResult(JsonRpcIdOf(turnReq), new { turn = new { id = "turn-9" } }));
        await sendTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal("turn-9", session.CurrentTurnId);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Async_answer_steers_running_turn_and_starts_a_new_turn_after_completion()
    {
        var fake = new FakeProcessHandle();
        await using var session = await StartHandshakenCodexAsync(fake, "thread-1");
        session.CurrentTurnId = "turn-1";

        var answerTask = session.SendMessageAsync("Question: Color?\nAnswer: Blue", TestContext.Current.CancellationToken);
        var steer = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"turn/steer\""), Timeout);
        using (var doc = JsonDocument.Parse(steer))
        {
            Assert.Equal("turn-1", doc.RootElement.GetProperty("params").GetProperty("expectedTurnId").GetString());
            Assert.Contains("Answer: Blue", doc.RootElement.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString());
        }
        fake.FeedStdout(JsonRpcResult(JsonRpcIdOf(steer), new { turnId = "turn-1" }));
        await answerTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal("turn-1", session.CurrentTurnId);
        Assert.DoesNotContain(fake.Writes, l => l.Contains("\"method\":\"turn/start\""));

        fake.FeedStdout("""{"method":"turn/completed","params":{"turn":{"id":"turn-1","status":"completed","items":[]}}}""");
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        readCts.CancelAfter(Timeout);
        await foreach (var output in session.Output.WithCancellation(readCts.Token))
            if (output is TurnEnded) break;
        Assert.Null(session.CurrentTurnId);

        var next = session.SendMessageAsync("A later answer", TestContext.Current.CancellationToken);
        var start = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"turn/start\""), Timeout);
        fake.FeedStdout(JsonRpcResult(JsonRpcIdOf(start), new { turn = new { id = "turn-2" } }));
        await next.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal("turn-2", session.CurrentTurnId);
    }

    [Fact]
    public async Task Answer_racing_turn_completion_retries_as_a_new_turn()
    {
        var fake = new FakeProcessHandle();
        await using var session = await StartHandshakenCodexAsync(fake, "thread-1");
        session.CurrentTurnId = "turn-1";
        var answer = session.SendMessageAsync("Blue", TestContext.Current.CancellationToken);
        var steer = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"turn/steer\""), Timeout);
        fake.FeedStdout("""{"method":"turn/completed","params":{"turn":{"id":"turn-1","status":"completed","items":[]}}}""");
        fake.FeedStdout(JsonSerializer.Serialize(new { id = JsonRpcIdOf(steer), error = new { code = -32600, message = "No active turn" } }));

        var start = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"turn/start\""), Timeout);
        fake.FeedStdout(JsonRpcResult(JsonRpcIdOf(start), new { turn = new { id = "turn-2" } }));
        await answer.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal("turn-2", session.CurrentTurnId);
    }

    [Fact]
    public async Task RollbackAsync_round_trips_thread_rollback_with_the_thread_id_and_turn_count()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenCodexAsync(fake, "thread-1");

        var rollbackTask = session.RollbackAsync(3, TestContext.Current.CancellationToken);
        var rollbackReq = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"thread/rollback\""), Timeout);
        Assert.Contains("\"threadId\":\"thread-1\"", rollbackReq);
        Assert.Contains("\"numTurns\":3", rollbackReq);

        // Awaited like any round-trip: completes only once the pump routes the response.
        Assert.False(rollbackTask.IsCompleted);
        fake.FeedStdout(JsonRpcResult(JsonRpcIdOf(rollbackReq), new { }));
        await rollbackTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task InterruptAsync_sends_turn_interrupt_with_the_thread_and_turn_ids()
    {
        var fake = new FakeProcessHandle();
        var session = await StartHandshakenCodexAsync(fake, "thread-1");

        var sendTask = session.SendMessageAsync("work", TestContext.Current.CancellationToken);
        var turnReq = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"turn/start\""), Timeout);
        fake.FeedStdout(JsonRpcResult(JsonRpcIdOf(turnReq), new { turn = new { id = "turn-7" } }));
        await sendTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.True(await session.InterruptAsync(TestContext.Current.CancellationToken));
        var interrupt = await fake.WaitForWriteAsync(l => l.Contains("\"method\":\"turn/interrupt\""), Timeout);
        Assert.Contains("thread-1", interrupt);
        Assert.Contains("turn-7", interrupt);

        await session.DisposeAsync();
    }
}
