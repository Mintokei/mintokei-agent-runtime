namespace Mintokei.AgentEngine.Contracts;

/// <summary>
/// A transcript message produced by the engine's stream parsers — the wire form the host maps
/// onto its own persisted entity. Field names deliberately mirror the host's message entity so
/// the mapping stays a straight copy, but this type carries no persistence navigations or EF
/// concerns: the engine emits it, the embedder owns storage.
///
/// <para><see cref="Id"/> is a fresh per-message id the engine stamps at parse time;
/// <see cref="AgentTaskId"/> tags the owning conversation; <see cref="CreatedAt"/> preserves
/// the parse-time ordering the host relies on when sorting a turn's messages.</para>
/// </summary>
public sealed class AgentMessage
{
    public Guid Id { get; set; }
    public Guid AgentTaskId { get; set; }

    public string? ExternalId { get; set; }
    public string? ParentToolUseId { get; set; }
    public MessageRole Role { get; set; }
    public MessageType Type { get; set; }
    public string? Content { get; set; }
    public MessageStatus? Status { get; set; }
    public long? DurationMs { get; set; }
    public string? Metadata { get; set; }
    public string? ImagesJson { get; set; }

    /// <summary>Normalized, provider-agnostic view of <see cref="ImagesJson"/>: attached images as
    /// directly-renderable sources. Derived from <see cref="ImagesJson"/> on read.</summary>
    public IReadOnlyList<ImageAttachment> Images => ImageNormalizer.Parse(ImagesJson);

    /// <summary>Provider's own id (UUID) for a user message, used later as a rewind/fork anchor.</summary>
    public string? ExternalUserMessageId { get; set; }

    public CommandExecutionData? CommandExecution { get; set; }
    public ToolCallData? ToolCall { get; set; }
    public UserInteractionData? UserInteraction { get; set; }
    public CompactBoundaryData? CompactBoundary { get; set; }
    public List<FileChangeData> FileChanges { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A single tool invocation attached to a message.</summary>
public sealed class ToolCallData
{
    public Guid Id { get; set; }
    public required string ToolName { get; set; }
    public string? ServerName { get; set; }
    public string? Arguments { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }

    /// <summary>Normalized, provider-agnostic view of <see cref="Arguments"/>: the flat JSON object's
    /// scalar entries as key/value pairs. Derived from <see cref="Arguments"/> on read.</summary>
    public IReadOnlyList<ToolArgument> ArgumentPairs => ToolArgumentNormalizer.Parse(Arguments);

    /// <summary>
    /// Normalized, provider-agnostic view of <see cref="Result"/>: MCP <c>{ content: [...] }</c>
    /// results, content-item arrays, plain text, and arbitrary JSON all collapse to a consistent
    /// list of segments, so a consumer needn't know each agent's result shape. Derived from
    /// <see cref="Result"/> on read; empty when there is no result.
    /// </summary>
    public IReadOnlyList<ContentSegment> ResultContent => ToolResultNormalizer.Parse(Result);
}

/// <summary>A single file edit attached to a message.</summary>
public sealed class FileChangeData
{
    public Guid Id { get; set; }
    public required string Path { get; set; }
    public required string Diff { get; set; }
    public FileChangeKind ChangeKind { get; set; }
}

/// <summary>A shell command execution attached to a message.</summary>
public sealed class CommandExecutionData
{
    public Guid Id { get; set; }
    public required string Command { get; set; }
    public required string Cwd { get; set; }
    public int? ExitCode { get; set; }
    public string? Output { get; set; }
}

/// <summary>
/// A permission / question / elicitation the CLI is blocked on. The request-side fields are set by
/// the parser; the decision fields (<see cref="Decision"/>, <see cref="DecisionData"/>,
/// <see cref="DecidedAt"/>) are filled by the host once the user answers.
/// </summary>
public sealed class UserInteractionData
{
    public Guid Id { get; set; }
    public required string RequestId { get; set; }
    public string? ToolName { get; set; }
    public string? ToolInput { get; set; }
    public string? Command { get; set; }
    public string? Cwd { get; set; }
    public string? Reason { get; set; }
    public string? Questions { get; set; }

    /// <summary>JSON array of CLI-suggested permission updates, surfaced as quick-action buttons.</summary>
    public string? Suggestions { get; set; }

    /// <summary>Normalized, provider-agnostic view of <see cref="Questions"/>. Derived on read.</summary>
    public IReadOnlyList<AgentQuestion> QuestionList => QuestionNormalizer.Parse(Questions);

    /// <summary>Normalized, provider-agnostic view of <see cref="Suggestions"/>. Derived on read.</summary>
    public IReadOnlyList<PermissionSuggestion> SuggestionList => SuggestionNormalizer.Parse(Suggestions);

    /// <summary>Backend-specific context needed to rebuild the wire reply on the durable recovery path.</summary>
    public string? ReplyContext { get; set; }

    public string? Decision { get; set; }
    public string? DecisionData { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>A context-window compaction boundary attached to a message.</summary>
public sealed class CompactBoundaryData
{
    public Guid Id { get; set; }
    public CompactTrigger Trigger { get; set; }
    public long? PreTokens { get; set; }
    public long? PostTokens { get; set; }
    public long? DurationMs { get; set; }

    /// <summary>Null while compaction is in flight, true/false once resolved.</summary>
    public bool? Success { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Plaintext summary (Claude populates; Codex leaves null — summary is encrypted).</summary>
    public string? SummaryText { get; set; }

    /// <summary>JSON array of tool names discovered before compaction (Claude only).</summary>
    public string? ToolsBeforeJson { get; set; }

    /// <summary>Normalized, provider-agnostic view of <see cref="ToolsBeforeJson"/>: the tool names
    /// as a string list. Derived from <see cref="ToolsBeforeJson"/> on read.</summary>
    public IReadOnlyList<string> ToolsBefore => JsonStringArray.Parse(ToolsBeforeJson);
}
