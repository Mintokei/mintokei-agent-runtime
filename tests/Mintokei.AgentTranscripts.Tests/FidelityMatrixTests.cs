using System.Text;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;
using Mintokei.AgentTranscripts.Claude;
using Mintokei.AgentTranscripts.Codex;
using Mintokei.AgentTranscripts.Copilot;

using Xunit;

namespace Mintokei.AgentTranscripts.Tests;

/// <summary>
/// What a conversation loses when it crosses.
///
/// <c>AgentMessage</c> has fourteen kinds and the readers produce most of them — Claude's parser
/// alone emits <c>FileChange</c>, <c>SubAgentExecution</c> and <c>CompactBoundary</c>. The writers
/// handle three to five each and turn the rest into assistant prose, which is a deliberate choice
/// (see the README's "What does not survive a write") but an unexamined one: nobody had checked
/// which kinds actually make it through, or whether the prose keeps the facts.
///
/// These tests pin the answer per store. A change to any writer either keeps the matrix or fails
/// here with the row that moved, so "we made conversion lossier" cannot happen quietly.
/// </summary>
public sealed class FidelityMatrixTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mintokei-fidelity", Guid.NewGuid().ToString("N"));

    public FidelityMatrixTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Cwd = "/tmp/fidelity-project";

    public static TheoryData<string> Stores => ["claude", "codex", "copilot"];

    private ITranscriptStore StoreFor(string name) => name switch
    {
        "claude" => new ClaudeTranscriptStore(Path.Combine(_root, name)),
        "codex" => new CodexTranscriptStore(Path.Combine(_root, name)),
        "copilot" => new CopilotTranscriptStore(Path.Combine(_root, name)),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    // ── the facts a moved conversation is supposed to carry ──────────────

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_plain_exchange_survives_intact(string store)
    {
        var read = await RoundTrip(store,
            User("what is the seal value?"),
            Assistant("The seal is KESTREL-77."));

        Assert.Equal(
            [(MessageRole.User, "what is the seal value?"), (MessageRole.Assistant, "The seal is KESTREL-77.")],
            read.Select(m => (m.Role, m.Content?.Trim())).ToArray());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_command_keeps_its_command_line_and_its_output(string store)
    {
        var read = await RoundTrip(store, User("check the port"), new AgentMessage
        {
            Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.CommandExecution,
            CommandExecution = new CommandExecutionData
            {
                Command = "grep -n 'port:' config.yaml",
                Cwd = Cwd,
                ExitCode = 0,
                Output = "3:port: 9090",
            },
        });

        var all = Flatten(read);
        Assert.Contains("grep -n 'port:' config.yaml", all);
        Assert.Contains("3:port: 9090", all);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_failed_command_does_not_read_as_a_successful_one(string store)
    {
        // The dangerous loss. A non-zero exit narrated without its status is a failure the next
        // agent will take for a success and build on.
        var read = await RoundTrip(store, User("run the tests"), new AgentMessage
        {
            Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.CommandExecution,
            CommandExecution = new CommandExecutionData
            {
                Command = "dotnet test",
                Cwd = Cwd,
                ExitCode = 1,
                Output = "Failed! - Failed: 3, Passed: 12",
            },
        });

        var all = Flatten(read);
        Assert.Contains("Failed! - Failed: 3, Passed: 12", all);

        // No format has a field for it: Claude records a boolean, and all three recover a number
        // by matching what a shell tool prints. So the writers print it.
        var command = read.FirstOrDefault(m => m.CommandExecution is not null)?.CommandExecution;
        Assert.Equal(1, command?.ExitCode);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_tool_call_keeps_its_name_arguments_and_result(string store)
    {
        var read = await RoundTrip(store, User("read the notes"), new AgentMessage
        {
            Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.ToolCall,
            ToolCall = new ToolCallData
            {
                ToolName = "Read",
                Arguments = """{"file_path":"/tmp/notes.txt"}""",
                Result = "seal: KESTREL-77",
            },
        });

        var all = Flatten(read);
        Assert.Contains("Read", all);
        Assert.Contains("/tmp/notes.txt", all);
        Assert.Contains("seal: KESTREL-77", all);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_tool_call_that_errored_carries_the_error(string store)
    {
        var read = await RoundTrip(store, User("read the missing file"), new AgentMessage
        {
            Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.ToolCall,
            ToolCall = new ToolCallData
            {
                ToolName = "Read",
                Arguments = """{"file_path":"/tmp/gone.txt"}""",
                Error = "ENOENT: no such file or directory",
            },
        });

        Assert.Contains("ENOENT: no such file or directory", Flatten(read));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task An_mcp_tool_keeps_the_server_it_belonged_to(string store)
    {
        // The next CLI has no such server. Knowing the call came from one is the difference
        // between "that tool is unavailable here" and silently retrying something that cannot work.
        var read = await RoundTrip(store, User("check the ticket"), new AgentMessage
        {
            Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.ToolCall,
            ToolCall = new ToolCallData
            {
                ToolName = "get_item_detail",
                ServerName = "targetprocess",
                Arguments = """{"id":4821}""",
                Result = "Bug 4821: ledger rounding",
            },
        });

        var all = Flatten(read);
        Assert.Contains("get_item_detail", all);
        Assert.Contains("Bug 4821: ledger rounding", all);
        // Written in Claude Code's own mcp__server__tool convention, which the Codex store's
        // reader already understood — only the writers were dropping it.
        var tool = read.FirstOrDefault(m => m.ToolCall is not null)?.ToolCall;
        Assert.Equal("targetprocess", tool?.ServerName);
        Assert.Contains("mcp__targetprocess__get_item_detail", all);
    }

    // ── the kinds with no wire form in any target ────────────────────────

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_file_edit_keeps_the_path_and_the_change(string store)
    {
        // FileChange is what Claude's Edit and Codex's apply_patch become on the way in, and no
        // writer has a form for it. Prose is acceptable; losing which file and what changed is not.
        var read = await RoundTrip(store, User("set the port to 9090"), new AgentMessage
        {
            Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.FileChange,
            Content = "Updated config.yaml: port 8080 → 9090",
            FileChanges =
            [
                new FileChangeData
                {
                    Path = "/tmp/fidelity-project/config.yaml",
                    Diff = "-port: 8080\n+port: 9090",
                    ChangeKind = FileChangeKind.Update,
                },
            ],
        });

        var all = Flatten(read);
        Assert.True(all.Contains("config.yaml", StringComparison.Ordinal),
            $"{store} lost the edited path entirely — the next agent has no idea which file moved.");
        Assert.Contains("9090", all);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_file_edit_with_no_prose_is_not_silently_dropped(string store)
    {
        // The writers fall back on Content, and a FileChange produced by a parser often has none —
        // the diff is the message. If that is dropped, an edit disappears from the record without
        // a word.
        var read = await RoundTrip(store, User("apply the patch"), new AgentMessage
        {
            Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.FileChange,
            FileChanges =
            [
                new FileChangeData
                {
                    Path = "/tmp/fidelity-project/ledger.cs",
                    Diff = "-var total = 0m;\n+var total = 0.00m;",
                    ChangeKind = FileChangeKind.Update,
                },
            ],
        });

        // Was the sharpest loss here: every writer fell back on Content, a FileChange from a
        // parser has none, and the edit left no trace at all. TranscriptNarration builds the
        // sentence the message never had.
        var all = Flatten(read);
        Assert.Contains("ledger.cs", all);
        Assert.Contains("0.00m", all);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task An_answered_question_keeps_the_question_and_the_answer(string store)
    {
        var read = await RoundTrip(store,
            new AgentMessage
            {
                Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.UserQuestion,
                Content = "Which database should I target?",
                UserInteraction = new UserInteractionData
                {
                    RequestId = "req-1",
                    Questions = """[{"question":"Which database?","options":[{"label":"postgres"},{"label":"sqlite"}]}]""",
                    Decision = "allow",
                    DecisionData = "postgres",
                },
            },
            User("postgres"));

        var all = Flatten(read);
        Assert.True(all.Contains("Which database", StringComparison.OrdinalIgnoreCase),
            $"{store} lost the question that was asked.");
        Assert.Contains("postgres", all);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_sub_agent_result_survives_as_something(string store)
    {
        var read = await RoundTrip(store, User("audit the handlers"), new AgentMessage
        {
            Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.SubAgentExecution,
            Content = "Sub-agent audited 12 handlers and found 2 missing null checks.",
        });

        Assert.Contains("2 missing null checks", Flatten(read));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Reasoning_is_not_passed_off_as_the_answer(string store)
    {
        // Thinking is provider-signed and cannot be reconstructed. Whatever a store does with it,
        // it must not end up indistinguishable from something the agent actually said.
        var read = await RoundTrip(store,
            User("is the ledger safe?"),
            new AgentMessage
            {
                Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.Reasoning,
                Content = "Maybe the rounding is wrong, I should check before saying so.",
            },
            Assistant("The ledger rounds correctly."));

        // Thinking has no wire form in any target, so it still crosses as prose — but marked, so
        // it cannot be read as a claim contradicting the answer beside it.
        var answers = read
            .Where(m => m.Role == MessageRole.Assistant && m.Type == MessageType.AgentMessage)
            .Select(m => m.Content?.Trim() ?? "")
            .ToList();

        Assert.DoesNotContain("Maybe the rounding is wrong, I should check before saying so.", answers);
        Assert.Contains(answers, a => a.StartsWith("(thinking)", StringComparison.Ordinal)
            && a.Contains("Maybe the rounding is wrong"));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_compaction_boundary_says_that_earlier_turns_are_gone(string store)
    {
        // After a compaction the summary IS the earlier conversation, and it crosses as an
        // ordinary user turn needing no help. The boundary beside it is the only record that
        // anything preceded it — Claude's store skipped that line as a file-only kind for a
        // while, so a moved conversation began with a summary and no sign it was one.
        var read = await RoundTrip(store,
            User("carry on from the summary"),
            new AgentMessage
            {
                Id = Guid.NewGuid(), Role = MessageRole.System, Type = MessageType.CompactBoundary,
                CompactBoundary = new CompactBoundaryData
                {
                    Trigger = CompactTrigger.Auto, PreTokens = 180_000, PostTokens = 20_000,
                },
            });

        Assert.Contains("compacted", Flatten(read), StringComparison.OrdinalIgnoreCase);
    }

    // ── shapes that break serialisers ────────────────────────────────────

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Unicode_code_fences_and_quotes_come_back_unchanged(string store)
    {
        const string awkward = """
            Résumé — 「テスト」 🎯 done.
            ```json
            {"quote": "he said \"go\"", "path": "C:\\work\\repo"}
            ```
            trailing	tab and 'single' "double"
            """;

        var read = await RoundTrip(store, User(awkward), Assistant("noted"));

        Assert.Equal(awkward, read.First(m => m.Role == MessageRole.User).Content);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_large_tool_result_is_not_truncated_or_rejected(string store)
    {
        // Copilot validates its own event log and rejects a malformed one silently; a big result
        // is where a store is most likely to produce one.
        var big = new StringBuilder();
        for (var i = 0; i < 20_000; i++)
            big.Append($"line {i}: the quick brown fox\n");
        var payload = big.ToString();

        var read = await RoundTrip(store, User("read the log"), new AgentMessage
        {
            Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.ToolCall,
            ToolCall = new ToolCallData { ToolName = "Read", Arguments = "{}", Result = payload },
        });

        Assert.Contains("line 19999: the quick brown fox", Flatten(read));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_tool_call_with_no_result_still_appears(string store)
    {
        // What an interrupted turn leaves behind: the call was made, the outcome is unknown. It
        // must not read as though the tool was never invoked.
        var read = await RoundTrip(store, User("edit the file"), new AgentMessage
        {
            Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.ToolCall,
            ToolCall = new ToolCallData
            {
                ToolName = "Edit",
                Arguments = """{"file_path":"/tmp/fidelity-project/app.cs"}""",
            },
        });

        var all = Flatten(read);
        if (store is "claude")
        {
            // Not a loss but a change of shape, and worth pinning because of what it leads to.
            // Claude's parser knows Edit is a file edit, so the call comes back as a FileChange
            // carrying the path — with no Content, because there was no result to narrate.
            var change = Assert.Single(read.Where(m => m.Type == MessageType.FileChange));
            Assert.Contains("app.cs", change.FileChanges.Single().Path);
            Assert.True(string.IsNullOrWhiteSpace(change.Content));
            return;
        }

        Assert.Contains("Edit", all);
        Assert.Contains("app.cs", all);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("copilot")]
    public async Task An_edit_survives_crossing_twice(string second)
    {
        // The two findings above compose into a hole neither shows alone.
        //
        // Hop one, into Claude: an Edit tool call becomes a FileChange with no prose, because the
        // path and diff are the message. Hop two, out again: every writer falls back on Content
        // for a kind it has no form for, and there is none — so the edit leaves no trace at all.
        //
        // One hop kept it; two hops and the file had never been touched, as far as the record
        // showed. Narration from the payload is what closes it.
        var afterClaude = await RoundTrip("claude", User("edit the file"), new AgentMessage
        {
            Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.ToolCall,
            ToolCall = new ToolCallData
            {
                ToolName = "Edit",
                Arguments = """{"file_path":"/tmp/fidelity-project/app.cs"}""",
            },
        });

        Assert.Contains("app.cs", Flatten(afterClaude));

        var afterSecond = await RoundTrip(second, [.. afterClaude]);
        Assert.Contains("app.cs", Flatten(afterSecond));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<AgentMessage>> RoundTrip(string store, params AgentMessage[] messages)
    {
        var target = StoreFor(store);
        var id = await target.WriteAsync(
            new StoredTranscript
            {
                Tool = AgentToolKey.ClaudeCodeCli,
                SessionId = "source",
                Cwd = Cwd,
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = messages,
            },
            new TranscriptWriteOptions { Cwd = Cwd },
            Ct);

        var read = await target.ReadAsync(id, Ct);
        Assert.NotNull(read);
        return read.Messages;
    }

    /// <summary>Every scrap of text a reader gave back, so "did this fact survive" is answerable
    /// without caring which field it landed in.</summary>
    private static string Flatten(IEnumerable<AgentMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            sb.AppendLine(m.Content);
            if (m.CommandExecution is { } c)
                sb.AppendLine(c.Command).AppendLine(c.Output).AppendLine(c.Cwd);
            if (m.ToolCall is { } t)
                sb.AppendLine(t.ToolName).AppendLine(t.ServerName).AppendLine(t.Arguments)
                  .AppendLine(t.Result).AppendLine(t.Error);
            foreach (var f in m.FileChanges)
                sb.AppendLine(f.Path).AppendLine(f.Diff);
            if (m.UserInteraction is { } u)
                sb.AppendLine(u.Questions).AppendLine(u.DecisionData).AppendLine(u.Reason);
        }
        return sb.ToString();
    }

    private static AgentMessage User(string text) => new()
    {
        Id = Guid.NewGuid(), Role = MessageRole.User, Type = MessageType.UserMessage, Content = text,
    };

    private static AgentMessage Assistant(string text) => new()
    {
        Id = Guid.NewGuid(), Role = MessageRole.Assistant, Type = MessageType.AgentMessage, Content = text,
    };
}
