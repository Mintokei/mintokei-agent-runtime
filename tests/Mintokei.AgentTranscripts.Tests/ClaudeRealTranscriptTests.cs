using Mintokei.AgentEngine.Contracts;
using Mintokei.AgentTranscripts.Claude;

using Xunit;

namespace Mintokei.AgentTranscripts.Tests;

/// <summary>
/// Reads a transcript Claude Code actually wrote, rather than one this library round-tripped
/// through its own writer. Round-trip tests only prove the writer and reader agree with each
/// other — they would happily pass while both misread the real format.
///
/// The fixture is a genuine session (paths sanitised, nothing else changed) and includes every
/// line kind the live stream never emits: <c>attachment</c>, <c>file-history-snapshot</c>,
/// <c>ai-title</c>, <c>last-prompt</c>, <c>queue-operation</c>.
/// </summary>
public sealed class ClaudeRealTranscriptTests : IDisposable
{
    private const string Cwd = "/tmp/fixture-project";
    private const string SessionId = "b42314bc-92c3-4503-a66f-95794b1a0534";

    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "mintokei-sessions-fixture", Guid.NewGuid().ToString("N"));

    public ClaudeRealTranscriptTests()
    {
        var dir = Path.Combine(_home, "projects", ClaudeTranscriptStore.SlugFor(Cwd));
        Directory.CreateDirectory(dir);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-real-transcript.jsonl"),
            Path.Combine(dir, $"{SessionId}.jsonl"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    [Fact]
    public async Task Reads_a_transcript_Claude_Code_actually_wrote()
    {
        var session = await new ClaudeTranscriptStore(_home)
            .ReadAsync(SessionId, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.Equal(Cwd, session.Cwd);
        Assert.False(string.IsNullOrWhiteSpace(session.Model));
        Assert.False(string.IsNullOrWhiteSpace(session.CliVersion));
        Assert.NotEqual(default, session.CreatedAt);

        // The human turn: only the store reads these — ParseUserEvent ignores plain-string content.
        var first = session.Messages[0];
        Assert.Equal(MessageRole.User, first.Role);
        Assert.Contains("notes.txt", first.Content);

        // The agent's own answer made it through.
        Assert.Contains(session.Messages, m =>
            m.Role == MessageRole.Assistant && (m.Content?.Contains("SPARROW-12") ?? false));
    }

    [Fact]
    public async Task Tool_calls_arrive_once_each_with_their_results_attached()
    {
        var session = await new ClaudeTranscriptStore(_home)
            .ReadAsync(SessionId, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        var tools = session.Messages
            .Where(m => m.ToolCall is not null || m.CommandExecution is not null)
            .ToList();
        Assert.NotEmpty(tools);

        // Every tool call is one message, not the in-progress/completed pair the live stream emits.
        var externalIds = tools.Where(m => m.ExternalId is not null).Select(m => m.ExternalId!).ToList();
        Assert.Equal(externalIds.Count, externalIds.Distinct().Count());

        // The Read of notes.txt must carry the file's contents, not an empty result.
        Assert.Contains(tools, m =>
            (m.ToolCall?.Result?.Contains("SPARROW-12") ?? false)
            || (m.CommandExecution?.Output?.Contains("SPARROW-12") ?? false));
    }

    [Fact]
    public async Task File_only_line_kinds_do_not_become_messages()
    {
        var session = await new ClaudeTranscriptStore(_home)
            .ReadAsync(SessionId, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        var raw = await File.ReadAllLinesAsync(
            Path.Combine(_home, "projects", ClaudeTranscriptStore.SlugFor(Cwd), $"{SessionId}.jsonl"),
            TestContext.Current.CancellationToken);

        // The fixture really does contain the kinds the stream never emits — otherwise this test
        // proves nothing.
        Assert.Contains(raw, l => l.Contains("\"type\":\"attachment\""));
        Assert.Contains(raw, l => l.Contains("\"type\":\"file-history-snapshot\""));
        Assert.Contains(raw, l => l.Contains("\"type\":\"queue-operation\""));

        Assert.True(session.Messages.Count < raw.Length);
        Assert.All(session.Messages, m =>
            Assert.True(m.Role is MessageRole.User or MessageRole.Assistant or MessageRole.Tool));
    }

    [Fact]
    public async Task The_title_comes_from_the_ai_title_line()
    {
        var session = await new ClaudeTranscriptStore(_home)
            .ReadAsync(SessionId, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.False(string.IsNullOrWhiteSpace(session.Title));
    }
}
