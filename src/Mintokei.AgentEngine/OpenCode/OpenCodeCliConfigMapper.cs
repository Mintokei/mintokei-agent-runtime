using Mintokei.AgentEngine.AgentTools;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.OpenCode;

/// <summary>
/// Maps camelCase agent-tool config keys to OpenCode CLI launch flags. Most flags
/// are launch-time only, so changes apply on next process restart.
/// </summary>
public static class OpenCodeCliConfigMapper
{
    public static IReadOnlyList<AgentToolConfigField> GetConfigFields() =>
    [
        new(
            Key: "agent",
            Label: "Agent",
            Type: AgentToolConfigFieldType.String,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            HelpText: "Named opencode agent profile to use (corresponds to --agent)."),
        new(
            Key: "dangerouslySkipPermissions",
            Label: "Auto-approve permissions",
            Type: AgentToolConfigFieldType.Bool,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            HelpText: "Auto-approve all non-explicitly-denied permissions. Dangerous — use only in sandboxed environments."),
    ];

    public sealed class MappedConfig
    {
        public string? Model { get; set; }
        public string? Agent { get; set; }
        public bool DangerouslySkipPermissions { get; set; }
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
                case "agent":
                    result.Agent = value;
                    break;
                case "dangerouslySkipPermissions":
                    result.DangerouslySkipPermissions = IsTruthy(value);
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the argv for <c>opencode acp</c>. The <c>acp</c> subcommand MUST come
    /// first — opencode parses the subcommand positionally before any flags.
    /// <para>
    /// NOTE: <c>opencode acp</c> does not accept <c>--model</c> or <c>--agent</c>
    /// (those live on the <c>run</c> / top-level commands). Model selection is sent
    /// per-turn via <c>_meta.opencode.modelId</c> on each <c>session/prompt</c> —
    /// see <c>OpenCodeBackend.BuildPromptParams</c>. Agent profile
    /// selection is currently launch-only and not exposed by acp; tracked as a future
    /// extension.
    /// </para>
    /// </summary>
    public static List<string> ToArgumentList(MappedConfig cfg, string? cwd)
    {
        var args = new List<string> { "acp" };

        if (!string.IsNullOrEmpty(cwd))
        {
            args.Add("--cwd");
            args.Add(cwd);
        }

        return args;
    }

    private static bool IsTruthy(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
