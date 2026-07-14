using Mintokei.AgentEngine.AgentTools;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine;

/// <summary>
/// Everything needed to spawn a session, with no dependency on <c>AgentTask</c> or the DB. The
/// DB-free replacement for the fields the execution service reads off the task row: the adapter
/// computes the DB/auth-derived bits (system prompt, MCP url+token, env) and fills them here, and the
/// backend's <c>BuildCommandLine</c> assembles them into the CLI invocation — it formats, never fetches.
/// </summary>
public sealed record AgentSessionSpec
{
    /// <summary>Which backend this session runs (selects the <c>IAgentBackend</c> in the launcher).</summary>
    public AgentToolKey Tool { get; init; }

    /// <summary>Working directory for the CLI process.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Agent-tool config (camelCase keys, e.g. <c>permissionMode</c>, <c>model</c>) mapped
    /// to CLI args by the backend's config mapper. Null/empty for defaults.</summary>
    public Dictionary<string, string?>? Config { get; init; }

    /// <summary>Appended/base system prompt — the snapshot assembled at task-creation time.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Resume an existing CLI session/thread id instead of starting fresh.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>Fork from an existing session id (Claude <c>--fork-session</c>).</summary>
    public string? ForkFromSessionId { get; init; }

    /// <summary>Rewind to a specific user message (Claude <c>--resume-session-at</c>).</summary>
    public string? ResumeSessionAt { get; init; }

    /// <summary>Mintokei MCP server URL, when MCP is enabled (null disables it).</summary>
    public string? McpUrl { get; init; }

    /// <summary>Whether the Mintokei MCP server is wired in. Explicit intent (rather than inferred from
    /// the token) so ACP — whose launch doesn't carry the token at all — needn't mint an unused one.</summary>
    public bool EnableMcp { get; init; }

    /// <summary>Bearer token for the Mintokei MCP server, minted by the adapter when
    /// <see cref="EnableMcp"/> is set (Claude <c>--mcp-config</c>, Codex <c>MINTOKEI_TOKEN</c>, ACP
    /// <c>session/new</c> mcpServers).</summary>
    public string? McpToken { get; init; }

    /// <summary>Extra environment variables the adapter supplies (workspace/task ids, etc.). Merged
    /// on top of the backend's static env.</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
}
