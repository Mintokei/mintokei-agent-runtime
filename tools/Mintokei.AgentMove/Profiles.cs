using System.Text.Json;
using System.Text.Json.Serialization;

using Mintokei.AgentEngine.AgentTools;

namespace Mintokei.AgentMove;

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
        .Where(kv => PermissionKeys.Contains(kv.Key))
        .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
        .Select(kv => $"{kv.Key}={kv.Value}");

    private static readonly HashSet<string> PermissionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // Claude
        "permissionMode", "allowedTools", "allowDangerouslySkipPermissions",
        // Codex
        "approvalPolicy", "access",
        // Copilot
        "autopilot", "allowAllPaths", "disableAskUser",
        // OpenCode
        "dangerouslySkipPermissions",
    };
}

/// <summary>Everything agentmove reads from disk.</summary>
public sealed record MoveConfig
{
    public Dictionary<string, Profile> Profiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Compress a conversation longer than this into a briefing. Null never compresses.</summary>
    public int? SummariseOver { get; init; }

    /// <summary>Handoff template; null uses <c>HandoffPrompt.DefaultTemplate</c>.</summary>
    public string? Handoff { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Loads the first config found: an explicit path, then <c>./agentmove.json</c>, then
    /// <c>$XDG_CONFIG_HOME/agentmove/config.json</c> (or <c>~/.config/…</c>). Returns
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
        yield return Path.Combine(Environment.CurrentDirectory, "agentmove.json");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        yield return Path.Combine(xdg, "agentmove", "config.json");
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

          // Compress a conversation longer than this into a briefing before moving it. Lossy;
          // omit to always move the real transcript.
          "summariseOver": 400

          // "handoff": "You were interrupted. Continue: {request}"
        }
        """;
}
