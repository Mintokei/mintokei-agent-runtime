using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentEngine.Codex;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

public sealed class CodexAdoptedTurnTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static AgentSession Adopted(FakeProcessHandle fake) => new(
        Guid.NewGuid(), fake, fake.Output,
        new CodexSessionProtocol(NullLogger.Instance, CodexConfigMapper.Map(new Dictionary<string, string?>()), null),
        new CodexInteractionReplyBuilder(), new AgentSessionOptions(), new CancellationTokenSource(),
        NullLogger.Instance, initialAgentSessionId: "surviving-thread");

    [Fact]
    public async Task First_adopted_send_reads_the_running_turn_then_steers_without_reporting_a_new_start()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeProcessHandle();
        await using var session = Adopted(fake);
        var starts = new List<string?>();
        session.TurnStartedAcknowledged = starts.Add;
        await session.AttachAsync(ct);
        Assert.Empty(fake.Writes); // Still no handshake during adoption.

        var send = session.SendMessageAsync("An update", ct);
        var read = await Request(fake, "thread/read");
        Assert.True(read.GetProperty("params").GetProperty("includeTurns").GetBoolean());
        Reply(fake, read, new { thread = new { turns = new[] { new { id = "existing-turn", status = "inProgress" } } } });
        var steer = await Request(fake, "turn/steer");
        Assert.Equal("existing-turn", steer.GetProperty("params").GetProperty("expectedTurnId").GetString());
        Reply(fake, steer, new { turnId = "existing-turn" });
        await send.WaitAsync(Timeout, ct);
        Assert.Equal("existing-turn", session.CurrentTurnId);
        Assert.Empty(starts);
        Assert.DoesNotContain(fake.Writes, l => l.Contains("\"method\":\"turn/start\""));

        // Recovery happens once per adoption, not on every mid-turn message.
        var again = session.SendMessageAsync("Another update", ct);
        var secondSteer = await Request(fake, "turn/steer", "Another update");
        Reply(fake, secondSteer, new { turnId = "existing-turn" });
        await again.WaitAsync(Timeout, ct);
        Assert.Single(fake.Writes, l => l.Contains("\"method\":\"thread/read\""));
    }

    [Fact]
    public async Task An_adopted_idle_thread_starts_a_genuinely_new_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeProcessHandle();
        await using var session = Adopted(fake);
        var starts = new List<string?>();
        session.TurnStartedAcknowledged = starts.Add;
        await session.AttachAsync(ct);
        var send = session.SendMessageAsync("Next instruction", ct);
        var read = await Request(fake, "thread/read");
        Reply(fake, read, new { thread = new { turns = new[] { new { id = "old", status = "completed" } } } });
        var start = await Request(fake, "turn/start");
        Reply(fake, start, new { turn = new { id = "new" } });
        await send.WaitAsync(Timeout, ct);
        Assert.Equal("new", Assert.Single(starts));
        Assert.Equal("new", session.CurrentTurnId);
    }

    [Fact]
    public async Task Completion_during_recovery_cannot_resurrect_the_finished_provider_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeProcessHandle();
        await using var session = Adopted(fake);
        await session.AttachAsync(ct);
        var send = session.SendMessageAsync("Next instruction", ct);
        var read = await Request(fake, "thread/read");
        // The RPC returns a snapshot taken BEFORE this completion notification.
        fake.FeedStdout("""{"method":"turn/completed","params":{"turn":{"id":"old","status":"completed","items":[]}}}""");
        Reply(fake, read, new { thread = new { turns = new[] { new { id = "old", status = "inProgress" } } } });
        var start = await Request(fake, "turn/start");
        Reply(fake, start, new { turn = new { id = "new" } });
        await send.WaitAsync(Timeout, ct);
        Assert.Equal("new", session.CurrentTurnId);
        Assert.DoesNotContain(fake.Writes, l => l.Contains("\"method\":\"turn/steer\""));
    }

    [Fact]
    public async Task A_failed_recovery_never_blindly_starts_a_turn_and_can_be_retried()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeProcessHandle();
        await using var session = Adopted(fake);
        await session.AttachAsync(ct);
        var send = session.SendMessageAsync("Instruction", ct);
        var read = await Request(fake, "thread/read");
        fake.FeedStdout(JsonSerializer.Serialize(new { id = read.GetProperty("id").GetInt32(), error = new { code = -32603, message = "Temporarily unavailable" } }));
        await Assert.ThrowsAsync<CodexJsonRpcException>(() => send);
        Assert.True(session.NeedsTurnStateRecovery);
        Assert.DoesNotContain(fake.Writes, l => l.Contains("\"method\":\"turn/start\""));

        var retry = session.SendMessageAsync("Instruction", ct);
        var retryRead = await Request(fake, "thread/read");
        Assert.NotEqual(read.GetProperty("id").GetInt32(), retryRead.GetProperty("id").GetInt32());
        Reply(fake, retryRead, new { thread = new { turns = new[] { new { id = "existing", status = "inProgress" } } } });
        var steer = await Request(fake, "turn/steer");
        Reply(fake, steer, new { turnId = "existing" });
        await retry.WaitAsync(Timeout, ct);
        Assert.False(session.NeedsTurnStateRecovery);
    }

    private static async Task<JsonElement> Request(FakeProcessHandle fake, string method, string? text = null)
    {
        var line = await fake.WaitForWriteAsync(l => l.Contains($"\"method\":\"{method}\"") && (text == null || l.Contains(text)), Timeout);
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }
    private static void Reply(FakeProcessHandle fake, JsonElement request, object result)
        => fake.FeedStdout(JsonSerializer.Serialize(new { id = request.GetProperty("id").GetInt32(), result }));
}
