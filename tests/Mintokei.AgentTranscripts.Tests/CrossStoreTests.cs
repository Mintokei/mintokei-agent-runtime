using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;
using Mintokei.AgentTranscripts.Claude;
using Mintokei.AgentTranscripts.Codex;

using Xunit;

namespace Mintokei.AgentTranscripts.Tests;

/// <summary>
/// Behaviour that only shows up when a transcript crosses from one CLI's store into another's.
/// </summary>
public sealed class CrossStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mintokei-cross-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StoredTranscript FromClaude(string model) => new()
    {
        Tool = AgentToolKey.ClaudeCodeCli,
        SessionId = "src",
        Cwd = "/tmp/demo-project",
        CreatedAt = DateTimeOffset.UtcNow,
        Model = model,
        CliVersion = "2.1.220",
        Messages =
        [
            new AgentMessage
            {
                Id = Guid.NewGuid(), Role = MessageRole.User,
                Type = MessageType.UserMessage, Content = "what is the vault value?",
            },
        ],
    };

    [Fact]
    public async Task A_foreign_model_is_not_stamped_into_the_target_transcript()
    {
        // Regression: writing a Claude transcript into Codex used to inherit Model from the source,
        // so the resumed session asked Codex for `claude-opus-5` and the API rejected the turn
        // outright — a hard 400, not a warning.
        var codex = new CodexTranscriptStore(Path.Combine(_root, "codex"));

        var id = await codex.WriteAsync(FromClaude("claude-opus-5"), ct: Ct);
        var written = await codex.ReadAsync(id, Ct);

        Assert.NotNull(written);
        Assert.NotEqual("claude-opus-5", written.Model);
        Assert.StartsWith("gpt-", written.Model);
    }

    [Fact]
    public async Task An_explicit_model_still_wins()
    {
        var codex = new CodexTranscriptStore(Path.Combine(_root, "codex"));

        var id = await codex.WriteAsync(
            FromClaude("claude-opus-5"), new TranscriptWriteOptions { Model = "gpt-5.4" }, Ct);
        var written = await codex.ReadAsync(id, Ct);

        Assert.NotNull(written);
        Assert.Equal("gpt-5.4", written.Model);
    }

    [Fact]
    public async Task Rewriting_within_the_same_store_keeps_its_own_model()
    {
        // The guard must not throw away a model that was already correct for this store.
        var codex = new CodexTranscriptStore(Path.Combine(_root, "codex"));
        var first = await codex.WriteAsync(
            FromClaude("ignored") with { Tool = AgentToolKey.CodexCli, Model = "gpt-5.5" }, ct: Ct);

        var read = await codex.ReadAsync(first, Ct);
        Assert.NotNull(read);
        var again = await codex.WriteAsync(read, ct: Ct);

        var rewritten = await codex.ReadAsync(again, Ct);
        Assert.NotNull(rewritten);
        Assert.Equal("gpt-5.5", rewritten.Model);
    }

    [Fact]
    public async Task Claude_does_the_same_with_a_Codex_model()
    {
        var claude = new ClaudeTranscriptStore(Path.Combine(_root, "claude"));
        // Claude records the model on assistant lines, not in a header, so the transcript needs an
        // assistant turn for the written model to be observable at all.
        var source = FromClaude("gpt-5.5") with
        {
            Tool = AgentToolKey.CodexCli,
            Messages =
            [
                new AgentMessage
                {
                    Id = Guid.NewGuid(), Role = MessageRole.User,
                    Type = MessageType.UserMessage, Content = "what is the vault value?",
                },
                new AgentMessage
                {
                    Id = Guid.NewGuid(), Role = MessageRole.Assistant,
                    Type = MessageType.AgentMessage, Content = "MARLIN-24.",
                },
            ],
        };

        var id = await claude.WriteAsync(source, ct: Ct);
        var written = await claude.ReadAsync(id, Ct);

        Assert.NotNull(written);
        Assert.NotEqual("gpt-5.5", written.Model);
        Assert.StartsWith("claude-", written.Model);
    }
}
