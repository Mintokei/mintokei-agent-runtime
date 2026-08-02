using Mintokei.AgentEngine.AgentTools;

namespace Mintokei.AgentMove;

/// <summary>
/// Which config keys each backend's mapper in <c>Mintokei.AgentEngine</c> actually consumes.
///
/// A key outside these sets is silently ignored by the engine, which for a permission-bearing
/// setting is the worst possible outcome: the profile says <c>access: read-only</c>, agentmove
/// prints <c>access=read-only</c>, and the agent runs with the CLI's default sandbox. So an
/// unrecognised key is an error here rather than something to shrug at.
///
/// These mirror the <c>switch</c> in each <c>*ConfigMapper</c>, which is a superset of that
/// mapper's <c>GetConfigFields()</c> — the field list is the UI's picker and omits keys like
/// <c>model</c> that every backend nonetheless accepts. Duplicated deliberately: the engine has no
/// single API for "is this key understood", and inferring it from mapper output cannot tell a key
/// that was ignored from one whose value happened to produce no argument.
/// </summary>
internal static class Backends
{
    public static IReadOnlySet<string> AcceptedKeys(AgentToolKey tool) => tool switch
    {
        AgentToolKey.ClaudeCodeCli => Claude,
        AgentToolKey.CodexCli => Codex,
        AgentToolKey.GithubCopilotCli => Copilot,
        AgentToolKey.OpenCodeCli => OpenCode,
        _ => Empty,
    };

    /// <summary>
    /// The keys that decide what the agent may do to the machine. Names are each CLI's own — there
    /// is no translation between them, so this is a union used only for highlighting.
    /// </summary>
    public static bool IsPermissionKey(string key) => Permission.Contains(key);

    /// <summary>
    /// The keys in <paramref name="config"/> that <paramref name="tool"/> does not understand,
    /// each paired with the closest key it does — <c>access</c> is Codex's setting in every other
    /// tool's vocabulary but its own, where it is <c>sandbox</c>.
    /// </summary>
    public static IReadOnlyList<(string Key, string? DidYouMean)> Unknown(
        AgentToolKey tool, IReadOnlyDictionary<string, string?> config)
    {
        var accepted = AcceptedKeys(tool);
        return config.Keys
            .Where(k => !accepted.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => (k, Nearest(k, accepted)))
            .ToList();
    }

    private static string? Nearest(string key, IReadOnlySet<string> accepted)
    {
        if (Aliases.TryGetValue(key, out var alias) && accepted.Contains(alias))
            return alias;
        // Otherwise a prefix match, which catches the shortenings people actually type.
        return accepted.FirstOrDefault(a =>
            a.StartsWith(key, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(a, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Names that mean the right thing but belong to another CLI, or to an older doc.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["access"] = "sandbox",           // Codex calls it sandbox
        ["autopilot"] = "mode",           // Copilot's autopilot is a value of `mode`, not a key
        ["approvalMode"] = "approvalPolicy",
        ["permissions"] = "permissionMode",
        ["reasoningEffort"] = "effort",
    };

    private static readonly HashSet<string> Claude = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "effort", "maxTurns", "allowedTools", "systemPromptFile",
        "permissionMode", "allowDangerouslySkipPermissions", "verbose",
    };

    private static readonly HashSet<string> Codex = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "modelProvider", "modelVerbosity", "effort", "summary", "personality",
        "collaborationMode", "approvalPolicy", "sandbox", "webSearch", "ephemeral", "noProjectDoc",
    };

    private static readonly HashSet<string> Copilot = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "effort", "mode", "disableAskUser", "disableBuiltinMcps",
        "enableAllGithubMcpTools", "allowAllPaths", "maxAutopilotContinues",
    };

    private static readonly HashSet<string> OpenCode = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "agent", "dangerouslySkipPermissions",
    };

    private static readonly HashSet<string> Empty = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Permission = new(StringComparer.OrdinalIgnoreCase)
    {
        "permissionMode", "allowedTools", "allowDangerouslySkipPermissions",   // Claude
        "approvalPolicy", "sandbox",                                           // Codex
        "mode", "allowAllPaths", "disableAskUser",                             // Copilot
        "dangerouslySkipPermissions",                                          // OpenCode
    };
}
