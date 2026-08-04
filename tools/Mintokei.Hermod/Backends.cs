using Mintokei.AgentEngine.AgentTools;

namespace Mintokei.Hermod;

/// <summary>
/// Which config keys each backend's mapper in <c>Mintokei.AgentEngine</c> actually consumes.
///
/// A key outside these sets is silently ignored by the engine, which for a permission-bearing
/// setting is the worst possible outcome: the profile says <c>access: read-only</c>, hermod
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
    /// Whether a profile pins its CLI to something that cannot modify the machine — the question
    /// asked before running one as a summariser.
    ///
    /// Summarising starts a second agent nobody asked to start, in the working directory of the
    /// session being moved. The engine denies every permission request it makes, but that only
    /// covers what the CLI stops to ask about: a profile saying <c>acceptEdits</c> or
    /// <c>sandbox: workspace-write</c> has already been granted the reach, and no request arrives.
    ///
    /// So the burden is inverted. A profile is quiet only when it says so; saying nothing means the
    /// CLI's own default, which differs per tool and per version, and is not something to infer.
    /// </summary>
    public static bool IsReadOnly(AgentToolKey tool, IReadOnlyDictionary<string, string?> config)
    {
        string? Value(string key) => config.TryGetValue(key, out var v) ? v?.Trim() : null;
        bool IsTrue(string key) => bool.TryParse(Value(key), out var b) && b;

        return tool switch
        {
            // `default` and `plan` both stop and ask, and the engine answers no.
            AgentToolKey.ClaudeCodeCli =>
                Value("permissionMode") is "default" or "plan"
                && !IsTrue("allowDangerouslySkipPermissions"),

            // The sandbox is the binding one; approvalPolicy only decides who is asked.
            AgentToolKey.CodexCli => Value("sandbox") == "read-only",

            AgentToolKey.GithubCopilotCli =>
                Value("mode") == "interactive" && !IsTrue("allowAllPaths"),

            // OpenCode has no read-only mode to pin, so it never qualifies.
            _ => false,
        };
    }

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
        "approvalPolicy", "sandbox", "webSearch", "noProjectDoc",
    };

    /// <summary>
    /// Keys the engine understands but hermod cannot deliver, with the reason. Accepting one
    /// would mean mapping it, sending nothing, and saying nothing — the same silence that let a
    /// permission setting go missing.
    /// </summary>
    public static bool Unsupported(string key, out string? why)
    {
        why = key.ToLowerInvariant() switch
        {
            // ThreadStart config. hermod only ever resumes an existing thread.
            "ephemeral" => "it only affects creating a thread, and hermod always resumes one",
            // TurnStart config with no command-line form, so only something driving Codex over
            // `codex app-server` could set it — which hermod no longer does.
            "collaborationmode" => "codex takes it only over its app-server protocol, and hermod "
                + "starts the CLI's own interface rather than driving it",
            _ => null,
        };
        return why is not null;
    }

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
