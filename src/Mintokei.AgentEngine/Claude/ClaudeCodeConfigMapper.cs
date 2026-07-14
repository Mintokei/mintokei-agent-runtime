using Mintokei.AgentEngine.AgentTools;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Claude;

/// <summary>
/// Maps camelCase agent tool config keys to Claude Code CLI arguments
/// and mid-session control request payloads.
/// </summary>
public static class ClaudeCodeConfigMapper
{
    /// <summary>
    /// UI-facing field descriptors for Claude Code. Keep in sync with
    /// <see cref="MapToCliArgs"/> and <see cref="SessionMutableKeys"/>.
    /// </summary>
    public static IReadOnlyList<AgentToolConfigField> GetConfigFields() =>
    [
        new(
            Key: "permissionMode",
            Label: "Permission mode",
            Type: AgentToolConfigFieldType.Select,
            ApplyMode: AgentToolConfigApplyMode.Immediate,
            Options:
            [
                new("default", "Default — ask for risky tools"),
                new("acceptEdits", "Accept edits — auto-approve file writes"),
                new("auto", "Auto — automatic permission handling"),
                new("dontAsk", "Don't ask — run pre-approved tools, deny the rest"),
                new("bypassPermissions", "Bypass — auto-approve everything"),
                new("plan", "Plan mode — read-only planning"),
            ],
            DefaultValue: "default"),
        new(
            Key: "effort",
            Label: "Effort",
            Type: AgentToolConfigFieldType.Select,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            Options:
            [
                new("low", "Low"),
                new("medium", "Medium"),
                new("high", "High"),
                new("xhigh", "Extra high"),
                new("max", "Max"),
            ],
            HelpText: "Reasoning budget per turn."),
        new(
            Key: "maxTurns",
            Label: "Max turns",
            Type: AgentToolConfigFieldType.Int,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            HelpText: "Cap on assistant turns per task."),
        new(
            Key: "verbose",
            Label: "Verbose logs",
            Type: AgentToolConfigFieldType.Bool,
            ApplyMode: AgentToolConfigApplyMode.Restart),
        new(
            Key: "allowDangerouslySkipPermissions",
            Label: "Skip permission prompts (dangerous)",
            Type: AgentToolConfigFieldType.Bool,
            ApplyMode: AgentToolConfigApplyMode.Restart,
            HelpText: "Runs tools without prompting. Use with care."),
    ];

    /// <summary>
    /// Converts merged config (camelCase schema keys) into <c>--kebab-case</c> CLI arguments
    /// suitable for the Claude Code CLI process startup.
    /// </summary>
    public static Dictionary<string, string?> MapToCliArgs(Dictionary<string, string?> raw)
    {
        var result = new Dictionary<string, string?>();

        foreach (var (key, value) in raw)
        {
            switch (key)
            {
                case "model":
                    if (!string.IsNullOrEmpty(value))
                        result["--model"] = value;
                    break;
                case "effort":
                    if (!string.IsNullOrEmpty(value))
                        result["--effort"] = value;
                    break;
                case "maxTurns":
                    if (!string.IsNullOrEmpty(value))
                        result["--max-turns"] = value;
                    break;
                case "allowedTools":
                    if (!string.IsNullOrEmpty(value))
                        result["--allowed-tools"] = value;
                    break;
                case "systemPromptFile":
                    if (!string.IsNullOrEmpty(value))
                        result["--system-prompt"] = value;
                    break;
                case "permissionMode":
                    if (!string.IsNullOrEmpty(value))
                        result["--permission-mode"] = value;
                    break;
                case "allowDangerouslySkipPermissions":
                    if (IsTruthy(value))
                        result["--allow-dangerously-skip-permissions"] = null;
                    break;
                case "verbose":
                    if (IsTruthy(value))
                        result["--verbose"] = null;
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Claude Code refuses dangerous permission-bypass modes when running as
    /// root unless it detects a sandboxed environment. Mintokei uses root-runner
    /// machines, so enable the sandbox hint only for launches that actually opt
    /// into those dangerous modes.
    /// </summary>
    public static bool RequiresSandboxEnvironment(IReadOnlyDictionary<string, string?> raw)
    {
        return raw.TryGetValue("permissionMode", out var permissionMode)
               && string.Equals(permissionMode, "bypassPermissions", StringComparison.OrdinalIgnoreCase)
            || raw.TryGetValue("allowDangerouslySkipPermissions", out var allowDangerouslySkipPermissions)
               && IsTruthy(allowDangerouslySkipPermissions);
    }

    /// <summary>
    /// Config keys that can be changed mid-session via the stream-json control protocol.
    /// </summary>
    private static readonly HashSet<string> SessionMutableKeys = ["model", "permissionMode"];

    /// <summary>
    /// Returns true if any of the changed keys between <paramref name="oldConfig"/> and
    /// <paramref name="newConfig"/> can be applied to a running Claude Code session
    /// via control requests (without restarting the process).
    /// </summary>
    public static bool HasSessionMutableChanges(
        Dictionary<string, string?> oldConfig,
        Dictionary<string, string?> newConfig)
    {
        foreach (var key in SessionMutableKeys)
        {
            oldConfig.TryGetValue(key, out var oldVal);
            newConfig.TryGetValue(key, out var newVal);
            if (!string.Equals(oldVal, newVal, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds a list of control request payloads for config keys that changed
    /// and support mid-session mutation.
    /// Each entry is a <c>(subtype, payload)</c> tuple ready to be sent as a
    /// <c>control_request</c> to the Claude Code CLI process.
    /// </summary>
    public static List<(string Subtype, object Payload)> GetControlRequests(
        Dictionary<string, string?> oldConfig,
        Dictionary<string, string?> newConfig)
    {
        var requests = new List<(string, object)>();

        if (TryGetChanged(oldConfig, newConfig, "model", out var model))
            requests.Add(("set_model", new { model }));

        if (TryGetChanged(oldConfig, newConfig, "permissionMode", out var mode))
            requests.Add(("set_permission_mode", new { mode }));

        return requests;
    }

    private static bool TryGetChanged(
        Dictionary<string, string?> oldConfig,
        Dictionary<string, string?> newConfig,
        string key,
        out string? newValue)
    {
        newConfig.TryGetValue(key, out newValue);
        oldConfig.TryGetValue(key, out var oldValue);
        return !string.Equals(oldValue, newValue, StringComparison.Ordinal);
    }

    private static bool IsTruthy(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
