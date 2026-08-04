using System.Text.Json.Nodes;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;
using Mintokei.AgentTranscripts.Claude;
using Mintokei.AgentTranscripts.Codex;
using Mintokei.AgentTranscripts.Copilot;

using Xunit;

namespace Mintokei.AgentTranscripts.Tests;

/// <summary>
/// Where a conversation was stopped by its provider rather than by anyone in it.
///
/// The shapes here are taken from a real session that hit a rate limit and a session limit
/// twenty minutes apart, kept going after both, and ran for another three thousand messages.
/// </summary>
public sealed class TranscriptFailureTests : IDisposable
{
    private const string Cwd = "/tmp/failure-fixture";
    private const string SessionId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "mintokei-failure-fixture", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    // ── reading ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_api_error_line_is_read_as_a_failure_not_as_something_the_agent_said()
    {
        // Claude files a refused turn as an ordinary assistant message with a flag beside it.
        // Read as prose, "You've hit your session limit" crosses into the next agent's history
        // as a sentence this one had supposedly written.
        var session = await ReadClaude(
            UserLine("deploy it"),
            ApiErrorLine("You've hit your session limit · resets 7:40am (UTC)"));

        var failure = Assert.Single(session!.Messages, m => m.Type == MessageType.Error);
        Assert.Equal("You've hit your session limit · resets 7:40am (UTC)", failure.Content);
        Assert.Equal(MessageStatus.Failed, failure.Status);
        Assert.DoesNotContain(session.Messages, m => m.Type == MessageType.AgentMessage);
    }

    [Fact]
    public async Task A_conversation_about_api_errors_is_not_a_conversation_that_hit_one()
    {
        // The session this was taken from spent an afternoon debugging a 401 and then hit a real
        // rate limit. Matching on the words would cut it in the wrong place, so the flag decides.
        var session = await ReadClaude(
            UserLine("why do I keep getting 429s?"),
            AssistantLine("The token returned API Error: 429 rate_limit, not 401 — so auth is fine."),
            ApiErrorLine("API Error: Server is temporarily limiting requests · Rate limited"));

        var failures = session!.FindFailures();

        var only = Assert.Single(failures);
        Assert.Contains("temporarily limiting", only.Text);
        Assert.Equal(TurnFailureKind.RateLimited, only.Kind);
    }

    // ── finding and cutting ──────────────────────────────────────────────

    [Fact]
    public void Failures_come_back_in_order_and_say_whether_the_session_survived_them()
    {
        // The one that matters. A limit is usually a scar, not an ending: you wait for the reset,
        // type "continue", and the session runs for hours. Cutting at a survived failure throws
        // all of that away, so a caller has to be able to see which is which.
        var t = With(
            User("start"), Failure("Rate limited"), User("Continue"), Assistant("carrying on"),
            User("more"), Failure("You've hit your session limit"));

        var failures = t.FindFailures();

        Assert.Equal(2, failures.Count);
        Assert.Equal(1, failures[0].Index);
        Assert.True(failures[0].Recovered);      // the agent spoke again afterwards
        Assert.False(failures[1].Recovered);     // this one ended it
    }

    [Fact]
    public void Cutting_drops_the_failure_and_everything_after_it()
    {
        var t = With(
            User("start"), Assistant("working"), Failure("Rate limited"),
            User("Continue"), Assistant("carrying on"));

        var cut = t.CutBefore(t.FindFailures()[0]);

        Assert.Equal(2, cut.Messages.Count);
        Assert.Equal("working", cut.Messages[^1].Content);
        Assert.DoesNotContain(cut.Messages, m => m.Type == MessageType.Error);
    }

    [Fact]
    public void Cutting_keeps_everything_that_makes_the_session_resumable()
    {
        var t = With(User("start"), Failure("Rate limited"));

        var cut = t.CutBefore(t.FindFailures()[0]);

        Assert.Equal(t.SessionId, cut.SessionId);
        Assert.Equal(t.Cwd, cut.Cwd);
        Assert.Equal(t.SourcePath, cut.SourcePath);
    }

    [Fact]
    public void A_clean_conversation_has_nothing_to_cut()
    {
        Assert.Empty(With(User("start"), Assistant("done")).FindFailures());
    }

    // ── writing ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("codex")]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task No_writer_puts_the_provider_s_failure_into_the_agent_s_mouth(string target)
    {
        // Even without a cut. The failure is transport, and an agent resuming a session whose
        // history says it announced a session limit will behave as though it had.
        var t = With(User("deploy it"), Failure("You've hit your session limit"), User("Continue"),
            Assistant("carrying on"));

        var home = Path.Combine(_home, target);
        Directory.CreateDirectory(home);
        ITranscriptStore store = target switch
        {
            "codex" => new CodexTranscriptStore(home),
            "copilot" => new CopilotTranscriptStore(home),
            _ => new ClaudeTranscriptStore(home),
        };

        var id = await store.WriteAsync(
            t, new TranscriptWriteOptions { Cwd = Cwd, RegisterInIndex = false },
            TestContext.Current.CancellationToken);

        var written = await store.ReadAsync(id, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            written!.Messages, m => (m.Content ?? "").Contains("session limit", StringComparison.Ordinal));
        Assert.Contains(written.Messages, m => (m.Content ?? "").Contains("carrying on", StringComparison.Ordinal));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task<StoredTranscript?> ReadClaude(params JsonObject[] lines)
    {
        var dir = Path.Combine(_home, "projects", ClaudeTranscriptStore.SlugFor(Cwd));
        Directory.CreateDirectory(dir);
        await File.WriteAllLinesAsync(
            Path.Combine(dir, $"{SessionId}.jsonl"),
            lines.Select(l => l.ToJsonString()),
            TestContext.Current.CancellationToken);

        return await new ClaudeTranscriptStore(_home)
            .ReadAsync(SessionId, TestContext.Current.CancellationToken);
    }

    private static JsonObject Line(string type, JsonNode message) => new()
    {
        ["type"] = type,
        ["uuid"] = Guid.NewGuid().ToString(),
        ["cwd"] = Cwd,
        ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        ["message"] = message,
    };

    private static JsonObject UserLine(string text) =>
        Line("user", new JsonObject { ["role"] = "user", ["content"] = text });

    private static JsonObject AssistantLine(string text) =>
        Line("assistant", new JsonObject
        {
            ["role"] = "assistant",
            ["model"] = "claude-sonnet-4-5",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        });

    private static JsonObject ApiErrorLine(string text)
    {
        var line = AssistantLine(text);
        line["isApiErrorMessage"] = true;
        return line;
    }

    private static AgentMessage User(string text) => new()
    {
        Id = Guid.NewGuid(), Role = MessageRole.User,
        Type = MessageType.UserMessage, Content = text,
    };

    private static AgentMessage Assistant(string text) => new()
    {
        Id = Guid.NewGuid(), Role = MessageRole.Assistant,
        Type = MessageType.AgentMessage, Content = text,
    };

    private static AgentMessage Failure(string text) => new()
    {
        Id = Guid.NewGuid(), Role = MessageRole.Assistant,
        Type = MessageType.Error, Status = MessageStatus.Failed, Content = text,
    };

    private static StoredTranscript With(params AgentMessage[] messages) => new()
    {
        Tool = AgentToolKey.ClaudeCodeCli, SessionId = "s", Cwd = Cwd,
        CreatedAt = DateTimeOffset.UtcNow, SourcePath = "/tmp/original.jsonl", Messages = messages,
    };
}
