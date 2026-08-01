using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentTranscripts;

/// <summary>
/// A whole agent conversation as it exists on disk, normalized to the engine's
/// <see cref="AgentMessage"/> contract.
///
/// This is deliberately the SAME message type <see cref="Mintokei.AgentEngine.IAgentSession"/>
/// emits for a live session: a transcript read from a CLI's own store and one streamed from a
/// running CLI produce identical values, so an embedder has one shape to handle either way.
/// The alternative — a bespoke transfer DTO — would mean two normalizations to keep in step
/// with every CLI release.
/// </summary>
public sealed record StoredTranscript
{
    /// <summary>Which CLI's store this came from (or is destined for).</summary>
    public required AgentToolKey Tool { get; init; }

    /// <summary>The CLI's own session/thread id — the value its <c>--resume</c> takes.</summary>
    public required string SessionId { get; init; }

    /// <summary>Working directory the session belongs to. Several stores key their layout off
    /// this (Claude Code derives its project directory name from it), so it is not just metadata.</summary>
    public required string Cwd { get; init; }

    /// <summary>When the CLI opened the session.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Model recorded in the transcript, when the store keeps one.</summary>
    public string? Model { get; init; }

    /// <summary>Human-readable title, when the store keeps one.</summary>
    public string? Title { get; init; }

    /// <summary>CLI version that produced the transcript. Worth carrying: these formats are
    /// undocumented and versioned, so it is the first thing to check when a read looks wrong.</summary>
    public string? CliVersion { get; init; }

    /// <summary>Git branch recorded at session start, when the store keeps one.</summary>
    public string? GitBranch { get; init; }

    /// <summary>
    /// Where this transcript was read from, when it came off disk. Conversion is lossy, so an agent
    /// or an operator that needs a detail which did not survive can go and read the original.
    /// Null for a transcript that was constructed rather than read.
    /// </summary>
    public string? SourcePath { get; init; }

    /// <summary>The transcript, in order.</summary>
    public IReadOnlyList<AgentMessage> Messages { get; init; } = [];
}

/// <summary>
/// Listing-level summary of a stored session — enough to show a picker without paying to parse
/// the whole transcript, which for a long session is megabytes.
/// </summary>
public sealed record StoredTranscriptInfo
{
    public required AgentToolKey Tool { get; init; }
    public required string SessionId { get; init; }
    public required string Cwd { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? Title { get; init; }

    /// <summary>First user turn, truncated — what pickers actually show.</summary>
    public string? FirstUserMessage { get; init; }
}

/// <summary>Knobs for <see cref="ITranscriptStore.WriteAsync"/>.</summary>
public sealed record TranscriptWriteOptions
{
    /// <summary>Session id to write under. Null generates one in the store's native id format
    /// (Claude uses UUIDv4, Codex UUIDv7), which is almost always what you want — reusing a
    /// source session's id across stores invites collisions.</summary>
    public string? SessionId { get; init; }

    /// <summary>Override the working directory the session is filed under.</summary>
    public string? Cwd { get; init; }

    /// <summary>Model to stamp into the transcript. Match what the CLI will actually run with:
    /// Codex warns on every turn when the recorded model differs from the resuming one.</summary>
    public string? Model { get; init; }

    /// <summary>CLI version to stamp into the transcript.</summary>
    public string? CliVersion { get; init; }

    /// <summary>Also register the session in the store's index, where it keeps one separate from
    /// the transcript (Codex's <c>threads</c> table, Copilot's <c>session-store.db</c>). Resume by
    /// explicit id generally works without it; interactive pickers generally do not.</summary>
    public bool RegisterInIndex { get; init; } = true;
}
