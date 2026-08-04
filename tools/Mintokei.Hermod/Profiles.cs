using System.Text.Json;
using System.Text.Json.Serialization;

using Mintokei.AgentEngine.AgentTools;

namespace Mintokei.Hermod;

/// <summary>
/// A named target: which CLI to continue in, and how to launch it.
///
/// <see cref="Config"/> is passed straight through to <c>AgentSessionSpec.Config</c>, which each
/// backend's config mapper already knows how to turn into that CLI's arguments. Reusing that
/// vocabulary rather than inventing one means a profile can express anything the engine can launch,
/// and gains new keys for free as the mappers grow.
///
/// Deliberately NOT translated between CLIs. <c>permissionMode</c> is Claude's, <c>approvalPolicy</c>
/// and <c>access</c> are Codex's, and there is no honest mapping between them — so each profile
/// states its own target's permissions, in writing, where they can be reviewed.
/// </summary>
public sealed record Profile
{
    /// <summary>claude | codex | copilot | opencode</summary>
    public string Tool { get; init; } = "claude";

    /// <summary>Optional description shown in the picker.</summary>
    public string? Description { get; init; }

    /// <summary>Engine config keys, e.g. <c>model</c>, <c>effort</c>, <c>permissionMode</c>.</summary>
    public Dictionary<string, string?> Config { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Arguments appended verbatim — the escape hatch for whatever the mappers do not cover.</summary>
    public List<string> ExtraArgs { get; init; } = [];

    public AgentToolKey ToolKey => Tool.Trim().ToLowerInvariant() switch
    {
        "claude" or "claude-code" => AgentToolKey.ClaudeCodeCli,
        "codex" => AgentToolKey.CodexCli,
        "copilot" => AgentToolKey.GithubCopilotCli,
        "opencode" or "open-code" => AgentToolKey.OpenCodeCli,
        _ => throw new InvalidOperationException(
            $"Unknown tool '{Tool}'. Use claude, codex, copilot, or opencode."),
    };

    /// <summary>Model, when the profile pins one.</summary>
    public string? Model => Config.TryGetValue("model", out var m) ? m : null;

    /// <summary>
    /// The settings that decide what the agent may do to the machine, for the confirmation the tool
    /// shows before launching. A hop should never widen the agent's reach without someone seeing it.
    /// </summary>
    public IEnumerable<string> PermissionSettings() => Config
        .Where(kv => Backends.IsPermissionKey(kv.Key))
        .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
        .Select(kv => $"{kv.Key}={kv.Value}");
}

/// <summary>Everything hermod reads from disk.</summary>
public sealed record MoveConfig
{
    public Dictionary<string, Profile> Profiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Compress a conversation longer than this into a briefing. Superseded by
    /// <see cref="Summary"/>; still read so configs written against the old shape keep working.
    /// </summary>
    public int? SummariseOver { get; init; }

    /// <summary>When to summarise and who writes it. Absent means never.</summary>
    public SummarySettings? Summary { get; init; }

    /// <summary>
    /// The summary settings after the old <c>summariseOver</c> is folded in.
    ///
    /// Both set is an <em>error</em> rather than a precedence rule. Picking one silently would
    /// leave the user believing a threshold applied that did not — the same failure as an ignored
    /// permission key, and worth stopping for on the same grounds.
    /// </summary>
    public SummarySettings EffectiveSummary()
    {
        if (Summary is not null && SummariseOver is not null)
        {
            throw new InvalidOperationException(
                "both \"summary\" and the older \"summariseOver\" are set — keep one. "
                + $"\"summariseOver\": {SummariseOver} is now {{ \"summary\": {{ \"when\": {SummariseOver} }} }}");
        }

        if (Summary is { } explicitSettings)
            return explicitSettings;
        if (SummariseOver is { } threshold)
            return new SummarySettings { When = SummaryTrigger.Over(threshold) };
        return new SummarySettings();
    }

    /// <summary>
    /// Handoff template. Absent uses the built-in wording; <c>""</c> means send nothing and let
    /// whoever picks the session up write their own first turn.
    ///
    /// The empty case is distinguished here rather than in <c>HandoffPrompt.Render</c>, which
    /// treats a blank template as "use the default" — right for a caller that always needs
    /// something to send, wrong for one whose user is about to type.
    /// </summary>
    public string? Handoff { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Loads the first config found: an explicit path, then <c>./hermod.json</c>, then
    /// <c>$XDG_CONFIG_HOME/hermod/config.json</c> (or <c>~/.config/…</c>). Returns
    /// <see cref="Fallback"/> when there is none, so the tool is useful before it is configured.
    /// </summary>
    public static (MoveConfig Config, string Origin) Load(string? explicitPath)
    {
        foreach (var path in CandidatePaths(explicitPath))
        {
            if (path is null || !File.Exists(path))
                continue;
            try
            {
                var parsed = JsonSerializer.Deserialize<MoveConfig>(File.ReadAllText(path), Json);
                if (parsed is null)
                    throw new InvalidOperationException("the file is empty");
                return (parsed, path);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // A broken config is worth stopping for. Falling back silently would launch an
                // agent with defaults the user believed they had overridden — including permissions.
                throw new InvalidOperationException($"{path}: {ex.Message}", ex);
            }
        }

        if (explicitPath is not null)
            throw new InvalidOperationException($"no config file at {explicitPath}");
        return (Fallback, "(built-in defaults)");
    }

    private static IEnumerable<string?> CandidatePaths(string? explicitPath)
    {
        yield return explicitPath;
        yield return Path.Combine(Environment.CurrentDirectory, "hermod.json");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        yield return Path.Combine(xdg, "hermod", "config.json");
    }

    /// <summary>
    /// Used when no config exists. Every profile here is conservative: the tool must not be the
    /// reason an agent gained more access than it had before.
    /// </summary>
    public static MoveConfig Fallback { get; } = new()
    {
        Profiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new Profile
            {
                Tool = "claude",
                Description = "Claude Code, asks before editing",
                Config = new(StringComparer.OrdinalIgnoreCase) { ["permissionMode"] = "default" },
            },
            ["codex"] = new Profile
            {
                Tool = "codex",
                Description = "Codex, asks before acting outside the workspace",
                Config = new(StringComparer.OrdinalIgnoreCase) { ["approvalPolicy"] = "on-request" },
            },
            ["copilot"] = new Profile
            {
                Tool = "copilot",
                Description = "GitHub Copilot CLI, asks before acting",
                // Copilot's setting is `mode`; "autopilot" is one of its values, not the key.
                Config = new(StringComparer.OrdinalIgnoreCase) { ["mode"] = "interactive" },
            },
        },
    };

    /// <summary>The starter file written by <c>--init</c>.</summary>
    public const string Sample = """
        {
          // Profiles are targets you can continue a session in. `config` keys go straight to the
          // engine's per-backend config mappers, so anything the engine can launch, a profile can
          // express. Permissions are NOT translated between CLIs — state each target's own.
          "profiles": {
            "claude": {
              "tool": "claude",
              "description": "Claude Code, asks before editing",
              "config": { "model": "claude-opus-5", "permissionMode": "default" }
            },
            "claude-fast": {
              "tool": "claude",
              "description": "smaller model, accepts edits",
              "config": { "model": "claude-sonnet-4-5", "permissionMode": "acceptEdits" }
            },
            "codex": {
              "tool": "codex",
              "description": "Codex, asks before acting outside the workspace",
              "config": { "model": "gpt-5.5", "approvalPolicy": "on-request" },
              "extraArgs": ["--skip-git-repo-check"]
            }
          },

          // Summarising is off, because moving the real transcript is what this tool is for. A
          // briefing is what you reach for when the conversation will not fit.
          //
          //   "when": "never" | "always" | <message count>
          //   "with": "mechanical"  — extracted from the transcript. Free, instant, shallow.
          //           a profile name — that agent reads the transcript and writes the handover.
          //                            Costs a model call; understands what mattered.
          //
          // "summary": { "when": 400, "with": "mechanical" },
          // "summary": { "when": "always", "with": "claude-fast" },

          // The opening turn. Omit for the built-in wording; "" to send nothing.
          // "handoff": "You were interrupted. Continue: {request}"
        }
        """;
}
