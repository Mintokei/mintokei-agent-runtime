using Mintokei.AgentEngine.AgentTools;

namespace Mintokei.AgentEngine.Codex;

/// <summary>Inference controls for the pinned Codex app-server. No environment access, tools,
/// MCP, hooks, skills, project instructions, resume or durable thread history.</summary>
public static class CodexSessionProfiles
{
    public static AgentSessionSpec Inference(AgentSessionSpec spec)
    {
        if (spec.Tool != AgentToolKey.CodexCli) throw new NotSupportedException("Expected a Codex session.");
        string[] overrides = [
            "web_search=\"disabled\"", "mcp_servers={}", "agents.enabled=false",
            "tools.update_plan.enabled=false", "tools.experimental_request_user_input.enabled=false",
            "skills.bundled.enabled=false", "skills.include_instructions=false", "skills.config=[]",
            "cloud.skills.enabled=false", "orchestrator.mcp.enabled=false", "history.persistence=\"none\"",
            .. new[] { "shell_tool", "view_image", "shell_snapshot", "hooks", "apps", "plugins", "remote_plugin",
                "multi_agent", "multi_agent_v2", "goals", "memories", "js_repl", "code_mode", "code_mode_only",
                "search_tool", "computer_use", "image_generation", "artifact", "skill_search", "remote_models",
                "request_permissions_tool", "send_message_to_user_async", "token_budget", "current_time_reminder", "sleep_tool" }
                .Select(feature => "features." + feature + "=false")
        ];
        return spec with
        {
            EnableMcp = false, McpUrl = null, McpToken = null,
            ResumeSessionId = null, ForkFromSessionId = null, ResumeSessionAt = null,
            Config = new()
            {
                ["model"] = spec.Config?.GetValueOrDefault("model"), ["effort"] = spec.Config?.GetValueOrDefault("effort"),
                ["approvalPolicy"] = "never", ["sandbox"] = "read-only", ["ephemeral"] = "true",
                ["noProjectDoc"] = "true", ["environmentAccess"] = "false", ["webSearch"] = "disabled"
            },
            ExtraArgs = overrides.SelectMany(value => new[] { "-c", value }).ToArray()
        };
    }
}
