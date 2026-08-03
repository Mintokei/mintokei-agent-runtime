using System.Text.Json;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;
using Mintokei.AgentTranscripts.Claude;

using Xunit;

namespace Mintokei.AgentTranscripts.Tests;

public sealed class ClaudeTranscriptStoreTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "mintokei-sessions-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    private ClaudeTranscriptStore Store() => new(_home);

    private static StoredTranscript SessionWith(params AgentMessage[] messages) => new()
    {
        Tool = AgentToolKey.ClaudeCodeCli,
        SessionId = "source-session",
        Cwd = "/tmp/demo.project",
        CreatedAt = DateTimeOffset.UtcNow,
        Messages = messages,
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

    [Fact]
    public async Task An_asked_question_is_read_with_its_answer()
    {
        // ClaudeCodeOutputParser drops AskUserQuestion and ExitPlanMode on purpose: a live stream
        // sends each twice, once as a tool_use and once as the control_request the host answers,
        // and counting both duplicates them. A transcript has no control_request — it is a wire
        // frame, never written to the file — so the tool_use is the only record, and skipping it
        // deleted the question while its answer survived as a tool named "unknown".
        var store = Store();
        var id = await store.WriteAsync(SessionWith(
            User("which database?"),
            new AgentMessage
            {
                Id = Guid.NewGuid(), Role = MessageRole.Tool, Type = MessageType.UserQuestion,
                ExternalId = "toolu_ask1",
                ToolCall = new ToolCallData
                {
                    ToolName = "AskUserQuestion",
                    Arguments = """{"questions":[{"question":"postgres or sqlite?"}]}""",
                    Result = "Your questions have been answered: postgres",
                },
            }), ct: TestContext.Current.CancellationToken);

        var read = await store.ReadAsync(id, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        var ask = Assert.Single(read.Messages.Where(m => m.ToolCall?.ToolName == "AskUserQuestion"));
        Assert.Contains("postgres or sqlite?", ask.ToolCall!.Arguments);
        Assert.Contains("postgres", ask.ToolCall.Result);
    }

    [Fact]
    public void SlugFor_replaces_every_non_alphanumeric_character()
    {
        // The store's whole layout hangs off this; Claude Code computes it the same way.
        Assert.Equal("-tmp-my-app", ClaudeTranscriptStore.SlugFor("/tmp/my.app"));
        Assert.Equal("-root-projects-mintokei-new",
            ClaudeTranscriptStore.SlugFor("/root/projects/mintokei-new"));
    }

    [Fact]
    public async Task Write_then_read_round_trips_a_plain_conversation()
    {
        var store = Store();
        var id = await store.WriteAsync(SessionWith(
            User("remember the codeword ALBATROSS"),
            Assistant("Noted — ALBATROSS.")), ct: TestContext.Current.CancellationToken);

        var read = await store.ReadAsync(id, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(AgentToolKey.ClaudeCodeCli, read.Tool);
        Assert.Equal("/tmp/demo.project", read.Cwd);
        Assert.Collection(read.Messages,
            m =>
            {
                Assert.Equal(MessageRole.User, m.Role);
                Assert.Contains("ALBATROSS", m.Content);
            },
            m =>
            {
                Assert.Equal(MessageRole.Assistant, m.Role);
                Assert.Contains("ALBATROSS", m.Content);
            });
    }

    [Fact]
    public async Task Write_lands_the_file_where_the_cli_looks_for_it()
    {
        var store = Store();
        var id = await store.WriteAsync(SessionWith(User("hi")), ct: TestContext.Current.CancellationToken);

        // claude --resume <id> scans <home>/projects/<slug>/<id>.jsonl — if this path is wrong
        // the conversion "succeeds" and the CLI silently never finds the session.
        var expected = Path.Combine(_home, "projects", "-tmp-demo-project", $"{id}.jsonl");
        Assert.True(File.Exists(expected), $"expected transcript at {expected}");

        foreach (var line in await File.ReadAllLinesAsync(expected, TestContext.Current.CancellationToken))
            JsonDocument.Parse(line).Dispose();   // every line must be standalone JSON
    }

    [Fact]
    public async Task Tool_calls_survive_as_a_tool_use_and_tool_result_pair()
    {
        var store = Store();
        var id = await store.WriteAsync(SessionWith(
            User("what is in the file?"),
            new AgentMessage
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.Assistant,
                Type = MessageType.ToolCall,
                ToolCall = new ToolCallData
                {
                    Id = Guid.NewGuid(),
                    ToolName = "Read",
                    Arguments = """{"file_path":"/tmp/demo.project/notes.txt"}""",
                    Result = "codeword: ALBATROSS",
                },
            }), ct: TestContext.Current.CancellationToken);

        var read = await store.ReadAsync(id, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        var call = Assert.Single(read.Messages, m => m.ToolCall is not null);
        Assert.Equal("Read", call.ToolCall!.ToolName);
        Assert.Contains("ALBATROSS", call.ToolCall.Result);
    }

    [Fact]
    public async Task Command_executions_round_trip_through_the_Bash_tool()
    {
        var store = Store();
        var id = await store.WriteAsync(SessionWith(
            new AgentMessage
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.Assistant,
                Type = MessageType.CommandExecution,
                CommandExecution = new CommandExecutionData
                {
                    Id = Guid.NewGuid(),
                    Command = "pytest -q",
                    Cwd = "/tmp/demo.project",
                    ExitCode = 0,
                    Output = "8 passed",
                },
            }), ct: TestContext.Current.CancellationToken);

        var read = await store.ReadAsync(id, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Contains(read.Messages, m =>
            (m.CommandExecution?.Command.Contains("pytest") ?? false)
            || (m.ToolCall?.Arguments?.Contains("pytest") ?? false));
    }

    [Fact]
    public async Task Reading_an_unknown_session_returns_null_rather_than_throwing()
    {
        Assert.Null(await Store().ReadAsync(Guid.NewGuid().ToString(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_corrupt_transcript_throws_instead_of_returning_half_a_conversation()
    {
        // Silently returning the parsable prefix is the dangerous outcome: the caller converts it
        // and the rest of the conversation disappears without anyone noticing.
        var dir = Path.Combine(_home, "projects", "-tmp-demo-project");
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString();
        await File.WriteAllLinesAsync(Path.Combine(dir, $"{id}.jsonl"),
        [
            """{"type":"user","cwd":"/tmp/demo.project","uuid":"u1","message":{"role":"user","content":"hi"}}""",
            "{ this is not json",
        ], TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<TranscriptStoreException>(() => Store().ReadAsync(id, TestContext.Current.CancellationToken));
        Assert.Contains("truncated or corrupt", ex.Message);
    }

    [Fact]
    public async Task Sidechain_and_meta_lines_are_left_out_of_the_conversation()
    {
        var dir = Path.Combine(_home, "projects", "-tmp-demo-project");
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString();
        await File.WriteAllLinesAsync(Path.Combine(dir, $"{id}.jsonl"),
        [
            """{"type":"user","cwd":"/tmp/demo.project","uuid":"u1","message":{"role":"user","content":"real turn"}}""",
            """{"type":"user","cwd":"/tmp/demo.project","uuid":"u2","isMeta":true,"message":{"role":"user","content":"harness noise"}}""",
            """{"type":"user","cwd":"/tmp/demo.project","uuid":"u3","isSidechain":true,"message":{"role":"user","content":"sub-agent"}}""",
            """{"type":"ai-title","aiTitle":"A demo session"}""",
        ], TestContext.Current.CancellationToken);

        var read = await Store().ReadAsync(id, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal("A demo session", read.Title);
        var only = Assert.Single(read.Messages);
        Assert.Contains("real turn", only.Content);
    }

    [Fact]
    public async Task List_reports_sessions_and_filters_by_cwd()
    {
        var store = Store();
        await store.WriteAsync(SessionWith(User("first")), ct: TestContext.Current.CancellationToken);

        var all = new List<StoredTranscriptInfo>();
        await foreach (var s in store.ListAsync(ct: TestContext.Current.CancellationToken))
            all.Add(s);
        Assert.Single(all);
        Assert.Equal("/tmp/demo.project", all[0].Cwd);
        Assert.Contains("first", all[0].FirstUserMessage);

        var none = new List<StoredTranscriptInfo>();
        await foreach (var s in store.ListAsync("/somewhere/else", TestContext.Current.CancellationToken))
            none.Add(s);
        Assert.Empty(none);
    }

    [Fact]
    public async Task Writing_without_a_cwd_is_refused()
    {
        var session = SessionWith(User("hi")) with { Cwd = "" };
        await Assert.ThrowsAsync<TranscriptStoreException>(() => Store().WriteAsync(session, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Derived_ids_are_stable_across_reads()
    {
        // Re-reading the same transcript must not look like a fresh set of messages.
        Assert.Equal(TranscriptIds.Derive("claude", "abc"), TranscriptIds.Derive("claude", "abc"));
        Assert.NotEqual(TranscriptIds.Derive("claude", "abc"), TranscriptIds.Derive("claude", "abd"));
        Assert.NotEqual(TranscriptIds.Derive("ab", "c"), TranscriptIds.Derive("a", "bc"));
    }

    [Fact]
    public void NewV7_ids_sort_in_creation_order()
    {
        var early = TranscriptIds.NewV7(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var late = TranscriptIds.NewV7(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(7, (early.ToString()[14] - '0'));
        Assert.True(string.CompareOrdinal(early.ToString(), late.ToString()) < 0);
    }
}
