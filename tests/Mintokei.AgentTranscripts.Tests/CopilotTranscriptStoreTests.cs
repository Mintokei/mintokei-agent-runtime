using System.Text.Json;

using Microsoft.Data.Sqlite;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;
using Mintokei.AgentTranscripts.Claude;
using Mintokei.AgentTranscripts.Copilot;

using Xunit;

namespace Mintokei.AgentTranscripts.Tests;

public sealed class CopilotTranscriptStoreTests : IDisposable
{
    private const string FixtureId = "3ff02a45-7b16-40df-b36e-62ba7711a502";
    private const string FixtureCwd = "/tmp/fixture-project";

    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "mintokei-copilot-tests", Guid.NewGuid().ToString("N"));

    public CopilotTranscriptStoreTests()
    {
        var dir = Path.Combine(_home, "session-state", FixtureId);
        Directory.CreateDirectory(dir);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "copilot-real-events.jsonl"),
            Path.Combine(dir, "events.jsonl"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    private CopilotTranscriptStore Store() => new(_home);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StoredTranscript TranscriptWith(params AgentMessage[] messages) => new()
    {
        Tool = AgentToolKey.GithubCopilotCli,
        SessionId = "src", Cwd = "/tmp/demo-project",
        CreatedAt = DateTimeOffset.UtcNow, Messages = messages,
    };

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

    // ── reading a transcript Copilot actually wrote ───────────────────────

    [Fact]
    public async Task Reads_a_transcript_Copilot_actually_wrote()
    {
        var t = await Store().ReadAsync(FixtureId, Ct);

        Assert.NotNull(t);
        Assert.Equal(AgentToolKey.GithubCopilotCli, t.Tool);
        Assert.Equal(FixtureCwd, t.Cwd);
        Assert.False(string.IsNullOrWhiteSpace(t.Model));
        Assert.False(string.IsNullOrWhiteSpace(t.CliVersion));
        Assert.NotEqual(default, t.CreatedAt);
        Assert.Contains(t.Messages, m => m.Role == MessageRole.User);
        Assert.Contains(t.Messages, m => m.Role == MessageRole.Assistant);
    }

    [Fact]
    public async Task The_system_prompt_is_not_part_of_the_conversation()
    {
        // system.message is the harness's own instructions, regenerated on every launch. Carrying
        // it across would paste one agent's system prompt into another agent's history.
        var t = await Store().ReadAsync(FixtureId, Ct);

        Assert.NotNull(t);
        Assert.DoesNotContain(t.Messages, m => m.Role == MessageRole.System);
        Assert.DoesNotContain(t.Messages, m => m.Content?.Contains("system prompt omitted") ?? false);
    }

    [Fact]
    public async Task Tool_calls_arrive_once_each_with_their_results_attached()
    {
        var t = await Store().ReadAsync(FixtureId, Ct);

        Assert.NotNull(t);
        var calls = t.Messages
            .Where(m => m.ToolCall is not null || m.CommandExecution is not null)
            .ToList();
        Assert.NotEmpty(calls);

        // start and complete are separate events; they must not become two messages.
        var ids = calls.Where(m => m.ExternalId is not null).Select(m => m.ExternalId!).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        // Results were attached rather than left dangling.
        Assert.Contains(calls, m =>
            !string.IsNullOrWhiteSpace(m.CommandExecution?.Output)
            || !string.IsNullOrWhiteSpace(m.ToolCall?.Result));
        Assert.All(calls, m => Assert.NotEqual(MessageStatus.InProgress, m.Status));
    }

    [Fact]
    public async Task Bash_calls_become_command_executions_with_an_exit_code()
    {
        var t = await Store().ReadAsync(FixtureId, Ct);

        Assert.NotNull(t);
        var command = t.Messages.First(m => m.CommandExecution is not null).CommandExecution!;
        Assert.False(string.IsNullOrWhiteSpace(command.Command));
        // Copilot has no exit-code field; it is parsed out of the output header, or inferred from
        // the success flag when the header is absent.
        Assert.NotNull(command.ExitCode);
    }

    [Fact]
    public async Task Opaque_reasoning_never_reaches_a_message()
    {
        var t = await Store().ReadAsync(FixtureId, Ct);

        Assert.NotNull(t);
        Assert.DoesNotContain(t.Messages, m => m.Content?.Contains("reasoningOpaque") ?? false);
        Assert.DoesNotContain(t.Messages, m => m.Content?.Contains("encryptedContent") ?? false);
    }

    [Fact]
    public async Task Reading_an_unknown_session_returns_null()
    {
        Assert.Null(await Store().ReadAsync(Guid.NewGuid().ToString(), Ct));
    }

    [Fact]
    public async Task A_corrupt_transcript_throws_instead_of_returning_half_a_conversation()
    {
        var id = Guid.NewGuid().ToString();
        var dir = Path.Combine(_home, "session-state", id);
        Directory.CreateDirectory(dir);
        await File.WriteAllLinesAsync(Path.Combine(dir, "events.jsonl"),
        [
            """{"type":"session.start","data":{"context":{"cwd":"/tmp/demo-project"}},"id":"a","timestamp":"2026-08-01T00:00:00.000Z"}""",
            "{ not json",
        ], Ct);

        var ex = await Assert.ThrowsAsync<TranscriptStoreException>(() => Store().ReadAsync(id, Ct));
        Assert.Contains("truncated or corrupt", ex.Message);
    }

    // ── writing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Write_then_read_round_trips_a_conversation()
    {
        var store = Store();
        var id = await store.WriteAsync(TranscriptWith(
            User("remember the codeword PETREL-5"),
            Assistant("Noted — PETREL-5.")), ct: Ct);

        var read = await store.ReadAsync(id, Ct);

        Assert.NotNull(read);
        Assert.Equal("/tmp/demo-project", read.Cwd);
        Assert.Collection(read.Messages,
            m => Assert.Contains("PETREL-5", m.Content),
            m => Assert.Contains("PETREL-5", m.Content));
    }

    [Fact]
    public async Task Timestamps_are_written_in_the_format_Copilot_can_parse()
    {
        // Round-trip "o" formatting yields seven fractional digits and a numeric offset, which
        // Copilot's YAML loader rejects — and it fails silently, logging to file rather than stderr.
        // Getting this wrong produces a session that exists and cannot be opened.
        var id = await Store().WriteAsync(TranscriptWith(User("hi")), ct: Ct);

        var workspace = await File.ReadAllTextAsync(
            Path.Combine(_home, "session-state", id, "workspace.yaml"), Ct);
        Assert.Matches(@"created_at: \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z", workspace);
        Assert.DoesNotContain("+00:00", workspace);
    }

    [Fact]
    public async Task Every_event_envelope_carries_a_uuid_id()
    {
        // Copilot validates this and refuses the whole session on the first bad envelope.
        var id = await Store().WriteAsync(TranscriptWith(User("hi"), Assistant("hello")), ct: Ct);

        var lines = await File.ReadAllLinesAsync(
            Path.Combine(_home, "session-state", id, "events.jsonl"), Ct);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var eventId = doc.RootElement.GetProperty("id").GetString();
            Assert.True(Guid.TryParse(eventId, out _), $"envelope id '{eventId}' is not a UUID");
        }
    }

    [Fact]
    public async Task Tool_results_are_written_as_an_object_not_a_json_string()
    {
        // Copilot deserialises result into a struct; a JSON string there fails the load.
        var id = await Store().WriteAsync(TranscriptWith(
            new AgentMessage
            {
                Id = Guid.NewGuid(), Role = MessageRole.Assistant,
                Type = MessageType.CommandExecution, Status = MessageStatus.Completed,
                CommandExecution = new CommandExecutionData
                {
                    Id = Guid.NewGuid(), Command = "ls -la", Cwd = "/tmp/demo-project",
                    ExitCode = 0, Output = "total 0",
                },
            }), ct: Ct);

        var lines = await File.ReadAllLinesAsync(
            Path.Combine(_home, "session-state", id, "events.jsonl"), Ct);
        var complete = lines.Select(l => JsonDocument.Parse(l).RootElement)
            .First(e => e.GetProperty("type").GetString() == "tool.execution_complete");

        var result = complete.GetProperty("data").GetProperty("result");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("total 0", result.GetProperty("content").GetString());
    }

    [Fact]
    public async Task The_directories_Copilot_expects_are_created_alongside_the_transcript()
    {
        var id = await Store().WriteAsync(TranscriptWith(User("hi")), ct: Ct);
        var dir = Path.Combine(_home, "session-state", id);

        foreach (var sub in new[] { "checkpoints", "files", "research" })
            Assert.True(Directory.Exists(Path.Combine(dir, sub)), $"{sub} was not created");
        Assert.True(File.Exists(Path.Combine(dir, "workspace.yaml")));
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
        // copilot --resume reads the transcript, so an absent index must not lose a session.
        var id = await Store().WriteAsync(TranscriptWith(User("hi")), ct: Ct);
        Assert.NotNull(await Store().ReadAsync(id, Ct));
    }

    [Fact]
    public async Task Listing_prefers_the_index_and_skips_sessions_with_no_transcript()
    {
        SeedIndex();
        var store = Store();
        var id = await store.WriteAsync(TranscriptWith(User("find me")), ct: Ct);

        // Copilot records a session before it has any turns; offering one with no events.jsonl
        // would hand the user something that cannot be read.
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(_home, "session-store.db")}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO sessions (id, cwd, summary, created_at, updated_at) "
                + "VALUES ('ghost', '/tmp/demo-project', 'never started', '2026-01-01 00:00:00', '2026-01-01 00:00:00')";
            cmd.ExecuteNonQuery();
        }

        var seen = new List<StoredTranscriptInfo>();
        await foreach (var s in store.ListAsync("/tmp/demo-project", Ct))
            seen.Add(s);

        var only = Assert.Single(seen);
        Assert.Equal(id, only.SessionId);
        Assert.Contains("find me", only.FirstUserMessage);
    }

    // ── the point of the package ──────────────────────────────────────────

    [Fact]
    public async Task A_Copilot_session_can_be_moved_into_Claude_Code()
    {
        var source = await Store().ReadAsync(FixtureId, Ct);
        Assert.NotNull(source);

        var claude = new ClaudeTranscriptStore(Path.Combine(_home, "claude"));
        var newId = await claude.WriteAsync(
            source, new TranscriptWriteOptions { Cwd = FixtureCwd }, Ct);
        var moved = await claude.ReadAsync(newId, Ct);

        Assert.NotNull(moved);
        Assert.Equal(AgentToolKey.ClaudeCodeCli, moved.Tool);
        Assert.Contains(moved.Messages, m => m.Role == MessageRole.User);
        Assert.Contains(moved.Messages, m =>
            m.ToolCall is not null || m.CommandExecution is not null);
        // The foreign model must not follow the conversation across.
        Assert.NotEqual(source.Model, moved.Model);
    }

    private void SeedIndex()
    {
        using var conn = new SqliteConnection($"Data Source={Path.Combine(_home, "session-store.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY, cwd TEXT, repository TEXT, host_type TEXT, branch TEXT,
                summary TEXT, created_at TEXT, updated_at TEXT);
            CREATE TABLE turns (
                id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL,
                turn_index INTEGER NOT NULL, user_message TEXT, assistant_response TEXT,
                timestamp TEXT, UNIQUE(session_id, turn_index));
            """;
        cmd.ExecuteNonQuery();
    }
}
