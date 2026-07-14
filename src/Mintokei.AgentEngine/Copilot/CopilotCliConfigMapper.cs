using Mintokei.AgentEngine.AgentTools;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Copilot;

/// <summary>
/// Maps camelCase agent tool config keys to GitHub Copilot CLI launch flags
/// and per-session parameters. Copilot's ACP is largely configured at process
/// launch (flags), so most changes are <see cref="AgentToolConfigApplyMode.Restart"/>.
/// </summary>
public static class CopilotCliConfigMapper
{
    public static IReadOnlyList<AgentToolConfigField> GetConfigFields() =>
    [
        new(
            Key: "effort",
            Label: "Reasoning effort",
            Type: AgentToolConfigFieldType.Select,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            Options:
            [
                new("low", "Low"),
                new("medium", "Medium"),
                new("high", "High"),
                new("xhigh", "Extra high"),
            ]),
        new(
            Key: "mode",
            Label: "Mode",
            Type: AgentToolConfigFieldType.Select,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            Options:
            [
                new("interactive", "Interactive"),
                new("plan", "Plan"),
                new("autopilot", "Autopilot"),
            ],
            HelpText: "Initial agent loop mode."),
        new(
            Key: "disableAskUser",
            Label: "Disable ask-user tool",
            Type: AgentToolConfigFieldType.Bool,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            HelpText: "Agent runs autonomously without asking clarifying questions."),
        new(
            Key: "disableBuiltinMcps",
            Label: "Disable built-in MCP servers",
            Type: AgentToolConfigFieldType.Bool,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            HelpText: "Disable github-mcp-server and other bundled MCP servers."),
        new(
            Key: "enableAllGithubMcpTools",
            Label: "Enable all GitHub MCP tools",
            Type: AgentToolConfigFieldType.Bool,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            HelpText: "Widen default GitHub MCP toolset (otherwise a subset is exposed)."),
        new(
            Key: "allowAllPaths",
            Label: "Allow any filesystem path",
            Type: AgentToolConfigFieldType.Bool,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            HelpText: "Disable cwd-based path restriction."),
        new(
            Key: "maxAutopilotContinues",
            Label: "Max autopilot continues",
            Type: AgentToolConfigFieldType.Int,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            HelpText: "Cap on autonomous continuation messages in autopilot mode."),
    ];

    public sealed class MappedConfig
    {
        public string? Model { get; set; }
        public string? Effort { get; set; }
        public string? Mode { get; set; }
        public bool DisableAskUser { get; set; }
        public bool DisableBuiltinMcps { get; set; }
        public bool EnableAllGithubMcpTools { get; set; }
        public bool AllowAllPaths { get; set; }
        public int? MaxAutopilotContinues { get; set; }
    }

    public static MappedConfig Map(Dictionary<string, string?> raw)
    {
        var result = new MappedConfig();

        foreach (var (key, value) in raw)
        {
            switch (key)
            {
                case "model":
                    result.Model = value;
                    break;
                case "effort":
                    result.Effort = value;
                    break;
                case "mode":
                    result.Mode = value;
                    break;
                case "disableAskUser":
                    result.DisableAskUser = IsTruthy(value);
                    break;
                case "disableBuiltinMcps":
                    result.DisableBuiltinMcps = IsTruthy(value);
                    break;
                case "enableAllGithubMcpTools":
                    result.EnableAllGithubMcpTools = IsTruthy(value);
                    break;
                case "allowAllPaths":
                    result.AllowAllPaths = IsTruthy(value);
                    break;
                case "maxAutopilotContinues":
                    if (int.TryParse(value, out var n))
                        result.MaxAutopilotContinues = n;
                    break;
            }
        }

        return result;
    }

    /// <summary>Converts mapped config into CLI flag/arg pairs for copilot --acp launch.</summary>
    public static Dictionary<string, string?> ToCliArguments(MappedConfig cfg)
    {
        var args = new Dictionary<string, string?>
        {
            ["--acp"] = null,
            ["--no-auto-update"] = null,
        };

        if (!string.IsNullOrEmpty(cfg.Model))
            args["--model"] = cfg.Model;

        if (!string.IsNullOrEmpty(cfg.Effort))
            args["--effort"] = cfg.Effort;

        if (!string.IsNullOrEmpty(cfg.Mode))
            args["--mode"] = cfg.Mode;

        if (cfg.DisableAskUser)
            args["--no-ask-user"] = null;

        if (cfg.DisableBuiltinMcps)
            args["--disable-builtin-mcps"] = null;

        if (cfg.EnableAllGithubMcpTools)
            args["--enable-all-github-mcp-tools"] = null;

        if (cfg.AllowAllPaths)
            args["--allow-all-paths"] = null;

        if (cfg.MaxAutopilotContinues is int max)
            args["--max-autopilot-continues"] = max.ToString();

        return args;
    }

    private static bool IsTruthy(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
