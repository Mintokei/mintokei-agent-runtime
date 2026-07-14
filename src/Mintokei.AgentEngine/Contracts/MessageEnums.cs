namespace Mintokei.AgentEngine.Contracts;

// Engine-owned wire enums. Deliberately parallel to the host's persistence enums so the
// engine contract never depends on the embedder's domain model; the embedder maps between
// the two at the sink. Member sets mirror the host enums 1:1.

/// <summary>Who authored a transcript message.</summary>
public enum MessageRole
{
    User,
    Assistant,
    Tool,
    System,
}

/// <summary>The kind of transcript message — drives how the host renders it.</summary>
public enum MessageType
{
    UserMessage,
    AgentMessage,
    CommandExecution,
    FileChange,
    ToolCall,
    Plan,
    Reasoning,
    WebSearch,
    PermissionRequest,
    UserQuestion,
    Other,
    Error,
    SubAgentExecution,
    CompactBoundary,
}

/// <summary>Lifecycle status of a message (mainly tool/command/interaction rows).</summary>
public enum MessageStatus
{
    InProgress,
    Completed,
    Failed,
    Declined,
    Cancelled,
}

/// <summary>Whether a file change added, updated, or deleted the file.</summary>
public enum FileChangeKind
{
    Add,
    Update,
    Delete,
}

/// <summary>What initiated a context-window compaction.</summary>
public enum CompactTrigger
{
    Manual,
    Auto,
}
