using Microsoft.Data.Sqlite;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;
using Mintokei.AgentTranscripts.Claude;
using Mintokei.AgentTranscripts.Codex;

using Xunit;

namespace Mintokei.AgentTranscripts.Tests;

/// <summary>
/// Listing behaviour an interactive picker depends on: titles worth showing, and not paying to
/// read every transcript on the machine to answer "what is in this directory?".
/// </summary>
public sealed class ListingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mintokei-listing-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StoredTranscript Transcript(AgentToolKey tool, string cwd, string text) => new()
    {
        Tool = tool, SessionId = "src", Cwd = cwd, CreatedAt = DateTimeOffset.UtcNow,
        Messages =
        [
            new AgentMessage
            {
                Id = Guid.NewGuid(), Role = MessageRole.User,
                Type = MessageType.UserMessage, Content = text,
            },
        ],
    };

    [Fact]
    public async Task Claude_listing_only_reads_the_directory_for_the_requested_cwd()
    {
        var home = Path.Combine(_root, "claude");
        var store = new ClaudeTranscriptStore(home);
        await store.WriteAsync(Transcript(AgentToolKey.ClaudeCodeCli, "/repo/one", "about one"), ct: Ct);
        await store.WriteAsync(Transcript(AgentToolKey.ClaudeCodeCli, "/repo/two", "about two"), ct: Ct);

        // A file that would throw if it were parsed. Filtering by cwd must never open it, because
        // it belongs to a different project directory.
        var poison = Path.Combine(home, "projects", ClaudeTranscriptStore.SlugFor("/repo/three"));
        Directory.CreateDirectory(poison);
        await File.WriteAllTextAsync(Path.Combine(poison, "boom.jsonl"), "{ not json\n", Ct);

        var seen = new List<StoredTranscriptInfo>();
        await foreach (var s in store.ListAsync("/repo/one", Ct))
            seen.Add(s);

        var only = Assert.Single(seen);
        Assert.Equal("/repo/one", only.Cwd);
        Assert.Contains("about one", only.FirstUserMessage);
    }

    [Fact]
    public async Task Claude_listing_without_a_cwd_still_sees_every_project()
    {
        var store = new ClaudeTranscriptStore(Path.Combine(_root, "claude"));
        await store.WriteAsync(Transcript(AgentToolKey.ClaudeCodeCli, "/repo/one", "one"), ct: Ct);
        await store.WriteAsync(Transcript(AgentToolKey.ClaudeCodeCli, "/repo/two", "two"), ct: Ct);

        var seen = new List<StoredTranscriptInfo>();
        await foreach (var s in store.ListAsync(ct: Ct))
            seen.Add(s);

        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task Codex_listing_uses_the_index_so_sessions_have_a_title()
    {
        var home = Path.Combine(_root, "codex");
        Directory.CreateDirectory(home);
        SeedThreadsIndex(Path.Combine(home, "state_5.sqlite"));

        var store = new CodexTranscriptStore(home);
        var id = await store.WriteAsync(
            Transcript(AgentToolKey.CodexCli, "/repo/one", "make it faster"), ct: Ct);

        var seen = new List<StoredTranscriptInfo>();
        await foreach (var s in store.ListAsync("/repo/one", Ct))
            seen.Add(s);

        var only = Assert.Single(seen);
        Assert.Equal(id, only.SessionId);
        // The rollout has no title anywhere; only the index does.
        Assert.False(string.IsNullOrWhiteSpace(only.Title));
        Assert.Contains("make it faster", only.Title);
    }

    [Fact]
    public async Task Codex_listing_falls_back_to_the_files_when_there_is_no_index()
    {
        // A missing index must not read as "you have no sessions" — the transcripts are the truth.
        var store = new CodexTranscriptStore(Path.Combine(_root, "codex-noindex"));
        var id = await store.WriteAsync(
            Transcript(AgentToolKey.CodexCli, "/repo/one", "still findable"), ct: Ct);

        var seen = new List<StoredTranscriptInfo>();
        await foreach (var s in store.ListAsync("/repo/one", Ct))
            seen.Add(s);

        var only = Assert.Single(seen);
        Assert.Equal(id, only.SessionId);
        Assert.Contains("still findable", only.FirstUserMessage);
    }

    private static void SeedThreadsIndex(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE threads (
                id TEXT PRIMARY KEY, rollout_path TEXT NOT NULL,
                created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL,
                source TEXT NOT NULL, model_provider TEXT NOT NULL, cwd TEXT NOT NULL,
                title TEXT NOT NULL, sandbox_policy TEXT NOT NULL, approval_mode TEXT NOT NULL,
                archived INTEGER NOT NULL DEFAULT 0, first_user_message TEXT NOT NULL DEFAULT '')
            """;
        cmd.ExecuteNonQuery();
    }
}
