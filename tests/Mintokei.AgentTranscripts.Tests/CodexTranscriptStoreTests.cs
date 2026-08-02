using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;
using Mintokei.AgentTranscripts.Claude;
using Mintokei.AgentTranscripts.Codex;

using Xunit;

namespace Mintokei.AgentTranscripts.Tests;

public sealed class CodexTranscriptStoreTests : IDisposable
{
    private const string FixtureCwd = "/tmp/fixture-project";
    private const string FixtureSessionId = "019fb9f5-9c57-7792-8ef8-708f80c587ed";

    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "mintokei-codex-tests", Guid.NewGuid().ToString("N"));

    public CodexTranscriptStoreTests()
    {
        var dir = Path.Combine(_home, "sessions", "2026", "07", "31");
        Directory.CreateDirectory(dir);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex-real-rollout.jsonl"),
            Path.Combine(dir, $"rollout-2026-07-31T20-55-09-{FixtureSessionId}.jsonl"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    private CodexTranscriptStore Store() => new(_home);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StoredTranscript TranscriptWith(params AgentMessage[] messages) => new()
    {
        Tool = AgentToolKey.CodexCli,
        SessionId = "source",
        Cwd = "/tmp/demo-project",
        CreatedAt = DateTimeOffset.UtcNow,
        Messages = messages,
    };

    private static AgentMessage User(string text) => new()
    {
        Id = Guid.NewGuid(), Role = MessageRole.User,
        Type = MessageType.UserMessage, Content = text,
    };

    // ── reading a transcript Codex actually wrote ─────────────────────────

    [Fact]
    public async Task Reads_a_rollout_Codex_actually_wrote()
    {
        var t = await Store().ReadAsync(FixtureSessionId, Ct);

        Assert.NotNull(t);
        Assert.Equal(AgentToolKey.CodexCli, t.Tool);
        Assert.Equal(FixtureCwd, t.Cwd);
        Assert.False(string.IsNullOrWhiteSpace(t.Model));
        Assert.False(string.IsNullOrWhiteSpace(t.CliVersion));
        Assert.NotEqual(default, t.CreatedAt);

        Assert.Contains(t.Messages, m =>
            m.Role == MessageRole.User && (m.Content?.Contains("cat notes.txt") ?? false));
        Assert.Contains(t.Messages, m =>
            m.Role == MessageRole.Assistant && (m.Content?.Contains("HERON-88") ?? false));
    }

    [Fact]
    public async Task Harness_preamble_turns_are_not_treated_as_the_conversation()
    {
        // The rollout opens with a `developer` permissions block and a synthetic `user` turn
        // carrying <recommended_plugins>. Both are regenerated on every launch, so carrying them
        // into another CLI would paste one agent's sandbox rules into a different agent's history.
        var t = await Store().ReadAsync(FixtureSessionId, Ct);

        Assert.NotNull(t);
        Assert.DoesNotContain(t.Messages, m => m.Content?.Contains("permissions instructions") ?? false);
        Assert.DoesNotContain(t.Messages, m => m.Content?.Contains("recommended_plugins") ?? false);

        var firstUser = t.Messages.First(m => m.Role == MessageRole.User);
        Assert.Contains("cat notes.txt", firstUser.Content);
    }

    [Fact]
    public async Task Exec_command_becomes_one_command_execution_with_its_exit_code()
    {
        var t = await Store().ReadAsync(FixtureSessionId, Ct);

        Assert.NotNull(t);
        var cmd = Assert.Single(t.Messages, m => m.CommandExecution is not null);
        Assert.Equal("cat notes.txt", cmd.CommandExecution!.Command);
        Assert.Equal(FixtureCwd, cmd.CommandExecution.Cwd);
        Assert.Equal(0, cmd.CommandExecution.ExitCode);
        Assert.Contains("HERON-88", cmd.CommandExecution.Output);
        // The call and its output are separate lines; they must not become two messages.
        Assert.Equal(MessageStatus.Completed, cmd.Status);
    }

    [Fact]
    public async Task Event_msg_lines_do_not_duplicate_the_conversation()
    {
        // event_msg mirrors response_item for the UI. Reading both would double every message.
        var t = await Store().ReadAsync(FixtureSessionId, Ct);

        Assert.NotNull(t);
        var assistantTexts = t.Messages
            .Where(m => m.Role == MessageRole.Assistant && m.Type == MessageType.AgentMessage)
            .Select(m => m.Content)
            .ToList();
        Assert.Equal(assistantTexts.Count, assistantTexts.Distinct().Count());
    }

    [Fact]
    public async Task Encrypted_reasoning_never_leaks_into_a_message()
    {
        var t = await Store().ReadAsync(FixtureSessionId, Ct);

        Assert.NotNull(t);
        // reasoning items carry provider-signed encrypted_content; only a plaintext summary may
        // travel, and this session produced none.
        Assert.DoesNotContain(t.Messages, m => m.Content?.Contains("encrypted_content") ?? false);
        Assert.DoesNotContain(t.Messages, m => m.Content?.StartsWith("gAAAAA") ?? false);
    }

    [Fact]
    public async Task Lists_the_session_with_its_first_real_user_turn()
    {
        var seen = new List<StoredTranscriptInfo>();
        await foreach (var s in Store().ListAsync(ct: Ct))
            seen.Add(s);

        var info = Assert.Single(seen);
        Assert.Equal(FixtureSessionId, info.SessionId);
        Assert.Equal(FixtureCwd, info.Cwd);
        Assert.Contains("cat notes.txt", info.FirstUserMessage);
    }

    // ── writing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Write_then_read_round_trips_a_conversation()
    {
        var store = Store();
        var id = await store.WriteAsync(TranscriptWith(
            User("what is the vault value?"),
            new AgentMessage
            {
                Id = Guid.NewGuid(), Role = MessageRole.Assistant,
                Type = MessageType.AgentMessage, Content = "HERON-88.",
            }), ct: Ct);

        var read = await store.ReadAsync(id, Ct);

        Assert.NotNull(read);
        Assert.Equal("/tmp/demo-project", read.Cwd);
        Assert.Collection(read.Messages,
            m => Assert.Equal(MessageRole.User, m.Role),
            m => Assert.Equal(MessageRole.Assistant, m.Role));
    }

    [Fact]
    public async Task Written_session_ids_are_time_ordered_like_the_ones_Codex_mints()
    {
        // Codex mints UUIDv7 and orders threads by id in places; a v4 would sort randomly.
        var id = await Store().WriteAsync(TranscriptWith(User("hi")), ct: Ct);
        Assert.Equal('7', id[14]);
    }

    [Fact]
    public async Task Write_lands_the_rollout_where_the_cli_looks_for_it()
    {
        var id = await Store().WriteAsync(TranscriptWith(User("hi")), ct: Ct);

        var matches = Directory.GetFiles(
            Path.Combine(_home, "sessions"), $"rollout-*{id}.jsonl", SearchOption.AllDirectories);
        var path = Assert.Single(matches);

        var lines = await File.ReadAllLinesAsync(path, Ct);
        // Codex needs the metadata header before any conversation item.
        Assert.Contains("\"type\":\"session_meta\"", lines[0]);
        Assert.Contains("\"type\":\"turn_context\"", lines[1]);
        foreach (var line in lines)
            System.Text.Json.JsonDocument.Parse(line).Dispose();
    }

    [Fact]
    public async Task Messages_are_written_to_the_presentation_channel_as_well()
    {
        // A rollout carries two parallel channels: `response_item` is what the model is given,
        // `event_msg` is what the interface replays. Writing only the first produced a session the
        // agent remembered perfectly and the TUI showed as empty — indistinguishable, to whoever
        // resumed it, from the move having failed.
        var store = Store();
        var id = await store.WriteAsync(TranscriptWith(
            User("what is the vault value?"),
            new AgentMessage
            {
                Id = Guid.NewGuid(), Role = MessageRole.Assistant,
                Type = MessageType.AgentMessage, Content = "HERON-88.",
            }), ct: Ct);

        var payloads = (await ReadRolloutAsync(id))
            .Where(l => l.RootElement.GetProperty("type").GetString() == "event_msg")
            .Select(l => l.RootElement.GetProperty("payload"))
            .ToList();

        Assert.Collection(payloads,
            p =>
            {
                Assert.Equal("user_message", p.GetProperty("type").GetString());
                Assert.Equal("what is the vault value?", p.GetProperty("message").GetString());
            },
            p =>
            {
                Assert.Equal("agent_message", p.GetProperty("type").GetString());
                Assert.Equal("HERON-88.", p.GetProperty("message").GetString());
            });
    }

    [Fact]
    public async Task Reading_back_ignores_the_presentation_channel()
    {
        // Both channels describe the same turn, so counting both would double every message.
        var store = Store();
        var id = await store.WriteAsync(TranscriptWith(
            User("one"),
            new AgentMessage
            {
                Id = Guid.NewGuid(), Role = MessageRole.Assistant,
                Type = MessageType.AgentMessage, Content = "two",
            }), ct: Ct);

        var read = await store.ReadAsync(id, Ct);

        Assert.NotNull(read);
        Assert.Equal(2, read.Messages.Count);
    }

    private async Task<IReadOnlyList<System.Text.Json.JsonDocument>> ReadRolloutAsync(string id)
    {
        var path = Directory
            .GetFiles(Path.Combine(_home, "sessions"), $"rollout-*{id}.jsonl", SearchOption.AllDirectories)
            .Single();
        var lines = await File.ReadAllLinesAsync(path, Ct);
        return [.. lines.Select(line => System.Text.Json.JsonDocument.Parse(line))];
    }

    [Fact]
    public async Task Command_executions_are_written_as_exec_command_calls()
    {
        var store = Store();
        var id = await store.WriteAsync(TranscriptWith(
            new AgentMessage
            {
                Id = Guid.NewGuid(), Role = MessageRole.Assistant,
                Type = MessageType.CommandExecution,
                CommandExecution = new CommandExecutionData
                {
                    Id = Guid.NewGuid(), Command = "pytest -q",
                    Cwd = "/tmp/demo-project", ExitCode = 0, Output = "8 passed",
                },
            }), ct: Ct);

        var read = await store.ReadAsync(id, Ct);

        Assert.NotNull(read);
        var cmd = Assert.Single(read.Messages, m => m.CommandExecution is not null);
        Assert.Equal("pytest -q", cmd.CommandExecution!.Command);
        Assert.Contains("8 passed", cmd.CommandExecution.Output);
    }

    [Fact]
    public async Task Writing_without_a_cwd_is_refused()
    {
        var t = TranscriptWith(User("hi")) with { Cwd = "" };
        await Assert.ThrowsAsync<TranscriptStoreException>(() => Store().WriteAsync(t, ct: Ct));
    }

    [Fact]
    public async Task A_missing_index_database_does_not_fail_the_write()
    {
        // Resume-by-id works from the file alone, so an absent or locked state_*.sqlite must not
        // lose a transcript that was otherwise written successfully.
        Assert.Null(Store().StateDatabase());
        var id = await Store().WriteAsync(TranscriptWith(User("hi")), ct: Ct);
        Assert.NotNull(await Store().ReadAsync(id, Ct));
    }

    [Fact]
    public async Task Reading_an_unknown_session_returns_null()
    {
        Assert.Null(await Store().ReadAsync(Guid.NewGuid().ToString(), Ct));
    }

    [Fact]
    public async Task A_corrupt_rollout_throws_instead_of_returning_half_a_conversation()
    {
        var dir = Path.Combine(_home, "sessions", "2026", "07", "31");
        var id = Guid.NewGuid().ToString();
        await File.WriteAllLinesAsync(Path.Combine(dir, $"rollout-x-{id}.jsonl"),
        [
            """{"timestamp":"2026-07-31T00:00:00Z","type":"session_meta","payload":{"id":"x","cwd":"/tmp/demo-project"}}""",
            "{ not json",
        ], Ct);

        var ex = await Assert.ThrowsAsync<TranscriptStoreException>(() => Store().ReadAsync(id, Ct));
        Assert.Contains("truncated or corrupt", ex.Message);
    }

    [Theory]
    [InlineData("mcp__mintokei__get_inbox", "mintokei")]
    [InlineData("exec_command", null)]
    [InlineData("update_plan", null)]
    public void Mcp_tool_names_expose_their_server(string toolName, string? expected)
        => Assert.Equal(expected, CodexTranscriptStore.McpServerOf(toolName));

    // ── the point of the whole package ────────────────────────────────────

    [Fact]
    public async Task A_Codex_session_can_be_moved_into_Claude_Code()
    {
        var claudeHome = Path.Combine(_home, "claude");
        var source = await Store().ReadAsync(FixtureSessionId, Ct);
        Assert.NotNull(source);

        var claude = new ClaudeTranscriptStore(claudeHome);
        var newId = await claude.WriteAsync(source, new TranscriptWriteOptions { Cwd = FixtureCwd }, Ct);
        var moved = await claude.ReadAsync(newId, Ct);

        Assert.NotNull(moved);
        Assert.Equal(AgentToolKey.ClaudeCodeCli, moved.Tool);
        // The conversation survived the crossing, both prose and tool activity.
        Assert.Contains(moved.Messages, m =>
            m.Role == MessageRole.User && (m.Content?.Contains("cat notes.txt") ?? false));
        Assert.Contains(moved.Messages, m => m.Content?.Contains("HERON-88") ?? false);
        Assert.Contains(moved.Messages, m =>
            (m.ToolCall?.Arguments?.Contains("cat notes.txt") ?? false)
            || (m.CommandExecution?.Command.Contains("cat notes.txt") ?? false));
    }
}
