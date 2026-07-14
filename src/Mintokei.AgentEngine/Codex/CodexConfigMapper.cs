using Mintokei.AgentEngine.AgentTools;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Codex;

/// <summary>
/// Maps camelCase agent tool config keys to Codex protocol structures:
/// thread/start params, turn/start params, and CLI flags.
/// </summary>
public static class CodexConfigMapper
{
    /// <summary>
    /// UI-facing field descriptors for Codex. Fields that land in
    /// <see cref="TurnStartConfig"/> apply on the next turn; fields consumed
    /// only at thread start or as CLI flags require a process restart.
    /// </summary>
    public static IReadOnlyList<AgentToolConfigField> GetConfigFields() =>
    [
        new(
            Key: "effort",
            Label: "Reasoning effort",
            Type: AgentToolConfigFieldType.Select,
            ApplyMode: AgentToolConfigApplyMode.NextTurn,
            Options:
            [
                new("low", "Low"),
                new("medium", "Medium"),
                new("high", "High"),
            ]),
        new(
            Key: "summary",
            Label: "Reasoning summary",
            Type: AgentToolConfigFieldType.Select,
            ApplyMode: AgentToolConfigApplyMode.NextTurn,
            Options:
            [
                new("auto", "Auto"),
                new("concise", "Concise"),
                new("detailed", "Detailed"),
            ]),
        new(
            Key: "personality",
            Label: "Personality",
            Type: AgentToolConfigFieldType.String,
            ApplyMode: AgentToolConfigApplyMode.NextTurn),
        new(
            Key: "collaborationMode",
            Label: "Collaboration mode",
            Type: AgentToolConfigFieldType.String,
            ApplyMode: AgentToolConfigApplyMode.NextTurn),
        new(
            Key: "modelVerbosity",
            Label: "Output verbosity",
            Type: AgentToolConfigFieldType.Select,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            Options:
            [
                new("low", "Low"),
                new("medium", "Medium"),
                new("high", "High"),
            ]),
        new(
            Key: "approvalPolicy",
            Label: "Approval policy",
            Type: AgentToolConfigFieldType.Select,
            ApplyMode: AgentToolConfigApplyMode.NextTurn,
            Options:
            [
                new("on-request", "On request"),
                new("on-failure", "On failure"),
                new("untrusted", "Untrusted"),
                new("never", "Never"),
            ]),
        new(
            Key: "sandbox",
            Label: "Sandbox",
            Type: AgentToolConfigFieldType.Select,
            ApplyMode: AgentToolConfigApplyMode.NextTurn,
            Options:
            [
                new("read-only", "Read-only"),
                new("workspace-write", "Workspace write"),
                new("danger-full-access", "Full access (dangerous)"),
            ]),
        new(
            Key: "webSearch",
            Label: "Web search",
            Type: AgentToolConfigFieldType.Bool,
            ApplyMode: AgentToolConfigApplyMode.Restart),
        new(
            Key: "ephemeral",
            Label: "Ephemeral thread",
            Type: AgentToolConfigFieldType.Bool,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            HelpText: "Discard thread state when the process exits."),
        new(
            Key: "noProjectDoc",
            Label: "Skip project docs",
            Type: AgentToolConfigFieldType.Bool,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            HelpText: "Don't auto-load AGENTS.md / repo docs."),
    ];

    public sealed class ThreadStartConfig
    {
        public string? Model { get; set; }
        public string? ModelProvider { get; set; }
        public string? ApprovalPolicy { get; set; }
        public string? Sandbox { get; set; }
        public string? Personality { get; set; }
        public bool? Ephemeral { get; set; }
        public ThreadNestedConfig? Config { get; set; }

        /// <summary>
        /// Maps to the Codex protocol <c>baseInstructions</c> field on thread/start
        /// and thread/resume. Set by the execution service from
        /// <c>AgentTask.ResolvedSystemPrompt</c> (assembled at task-creation time
        /// from Agent.SystemPrompt + attached AgentSkill bodies + spawnable list).
        /// </summary>
        public string? BaseInstructions { get; set; }
    }

    public sealed class ThreadNestedConfig
    {
        public string? ModelVerbosity { get; set; }
        public string? WebSearch { get; set; }
        public string? ModelReasoningEffort { get; set; }
        public string? ModelReasoningSummary { get; set; }
    }

    public sealed class TurnStartConfig
    {
        public string? Model { get; set; }
        public string? Effort { get; set; }
        public string? Summary { get; set; }
        public string? Personality { get; set; }
        public string? ApprovalPolicy { get; set; }
        public IReadOnlyDictionary<string, object?>? SandboxPolicy { get; set; }
        public string? CollaborationMode { get; set; }
    }

    public sealed class CliFlags
    {
        public bool NoProjectDoc { get; set; }
    }

    public sealed class MappedConfig
    {
        public ThreadStartConfig ThreadStart { get; set; } = new();
        public TurnStartConfig TurnStart { get; set; } = new();
        public CliFlags Cli { get; set; } = new();
    }

    public static MappedConfig Map(Dictionary<string, string?> raw)
    {
        var result = new MappedConfig();

        foreach (var (key, value) in raw)
        {
            switch (key)
            {
                case "model":
                    result.ThreadStart.Model = value;
                    result.TurnStart.Model = value;
                    break;
                case "modelProvider":
                    result.ThreadStart.ModelProvider = value;
                    break;
                case "approvalPolicy":
                    result.ThreadStart.ApprovalPolicy = value;
                    result.TurnStart.ApprovalPolicy = value;
                    break;
                case "sandbox":
                    result.ThreadStart.Sandbox = value;
                    result.TurnStart.SandboxPolicy = BuildSandboxPolicy(value);
                    break;
                case "personality":
                    result.ThreadStart.Personality = value;
                    result.TurnStart.Personality = value;
                    break;
                case "ephemeral":
                    result.ThreadStart.Ephemeral = IsTruthy(value);
                    break;
                case "effort":
                    result.TurnStart.Effort = value;
                    // Also feed into thread config as model_reasoning_effort
                    EnsureNestedConfig(result.ThreadStart).ModelReasoningEffort = value;
                    break;
                case "summary":
                    result.TurnStart.Summary = value;
                    EnsureNestedConfig(result.ThreadStart).ModelReasoningSummary = value;
                    break;
                case "modelVerbosity":
                    EnsureNestedConfig(result.ThreadStart).ModelVerbosity = value;
                    break;
                case "webSearch":
                    EnsureNestedConfig(result.ThreadStart).WebSearch = value;
                    break;
                case "collaborationMode":
                    result.TurnStart.CollaborationMode = value;
                    break;
                case "noProjectDoc":
                    result.Cli.NoProjectDoc = IsTruthy(value);
                    break;
            }
        }

        return result;
    }

    private static ThreadNestedConfig EnsureNestedConfig(ThreadStartConfig cfg)
    {
        cfg.Config ??= new ThreadNestedConfig();
        return cfg.Config;
    }

    /// <summary>
    /// Codex thread APIs use a flat sandbox enum, while turn/start expects a
    /// structured sandboxPolicy object. Convert the UI-facing string into the
    /// per-turn protocol shape so sandbox changes can apply on the next turn.
    /// </summary>
    internal static IReadOnlyDictionary<string, object?>? BuildSandboxPolicy(string? sandbox)
        => sandbox switch
        {
            "danger-full-access" => new Dictionary<string, object?>
            {
                ["type"] = "dangerFullAccess",
            },
            "read-only" => new Dictionary<string, object?>
            {
                ["type"] = "readOnly",
                ["access"] = new Dictionary<string, object?>
                {
                    ["type"] = "fullAccess",
                },
            },
            "workspace-write" => new Dictionary<string, object?>
            {
                ["type"] = "workspaceWrite",
                ["networkAccess"] = false,
                ["readOnlyAccess"] = new Dictionary<string, object?>
                {
                    ["type"] = "fullAccess",
                },
                ["writableRoots"] = Array.Empty<string>(),
            },
            _ => null,
        };

    private static bool IsTruthy(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
