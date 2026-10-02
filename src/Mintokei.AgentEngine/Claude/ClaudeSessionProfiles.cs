namespace Mintokei.AgentEngine.Claude;

/// <summary>Explicit fresh-session flags for the supported Claude CLI image. Authentication remains
/// native or supplied through the host's proxy environment; these flags never read credentials.</summary>
public static class ClaudeSessionProfiles
{
    public static string[] NativeArguments(bool isolatedWorkspace) =>
    [
        "--no-session-persistence", "--strict-mcp-config", "--setting-sources", "",
        "--settings", "{\"autoMemoryEnabled\":false,\"disableAllHooks\":true}",
        .. isolatedWorkspace ? new[] { "--restricted", "--tools", "", "--disable-slash-commands" } : []
    ];

    /// <summary>Inference only: no tools, MCP, hooks, project settings, skills, resume or transcript persistence.
    /// The host still supplies a trusted working directory, model and authentication environment.</summary>
    public static AgentSessionSpec Inference(AgentSessionSpec spec)
    {
        if (spec.Tool != AgentTools.AgentToolKey.ClaudeCodeCli)
            throw new NotSupportedException("The inference-only profile currently supports Claude Code. Other CLIs require a verified tool-disable mapping.");
        return spec with
        {
            EnableMcp = false, McpUrl = null, McpToken = null,
            ResumeSessionId = null, ForkFromSessionId = null, ResumeSessionAt = null,
            Config = new Dictionary<string, string?>
            {
                ["model"] = spec.Config?.GetValueOrDefault("model"),
                ["effort"] = spec.Config?.GetValueOrDefault("effort"), ["permissionMode"] = "default"
            },
            ExtraArgs = [.. NativeArguments(true), "--mcp-config", "{\"mcpServers\":{}}"]
        };
    }
}
