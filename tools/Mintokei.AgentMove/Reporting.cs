using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;

namespace Mintokei.AgentMove;

/// <summary>
/// Says truthfully which of a profile's settings will be in force, because the answer depends on
/// how the session is started.
///
/// With <c>--launch</c> the whole <c>config</c> goes to <c>AgentSessionSpec.Config</c> and the
/// backend's mapper turns it into how the CLI is run, so all of it applies. Without it, agentmove
/// prints a command for the user to run, and a command line can only carry what that CLI's own
/// resume invocation accepts: most of Claude's config, the model for Copilot, and — because the
/// engine drives Codex over <c>codex app-server</c> rather than by flags — nothing at all for Codex.
///
/// Printing <c>approvalPolicy=on-request</c> beside a command that does not carry it is worse than
/// printing nothing: it reads as a statement about what the agent is allowed to do.
/// </summary>
internal static class Reporting
{
    /// <summary>The flags the printed resume command carries, and the config keys it drops.</summary>
    public static (IReadOnlyList<string> Args, IReadOnlyList<string> Dropped) CommandLine(
        AgentToolKey tool, Profile profile)
    {
        var config = new Dictionary<string, string?>(profile.Config, StringComparer.OrdinalIgnoreCase);
        if (config.Count == 0)
            return ([], []);

        switch (tool)
        {
            case AgentToolKey.ClaudeCodeCli:
            {
                // The engine's own mapper, so the printed command stays right as the mapper grows.
                var mapped = ClaudeCodeConfigMapper.MapToCliArgs(config);
                var args = mapped
                    .Select(kv => kv.Value is null ? kv.Key : $"{kv.Key} {Quote(kv.Value)}")
                    .ToList();
                // A key the mapper ignored produced no flag, so the command does not carry it.
                // Compared against the accepted set rather than the mapper's output, because a key
                // whose value produced no argument (an empty string, a false bool) is still a key
                // the user asked for and did not get.
                var accepted = Backends.AcceptedKeys(AgentToolKey.ClaudeCodeCli);
                var dropped = config.Keys
                    .Where(k => !accepted.Contains(k))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return (args, dropped);
            }

            case AgentToolKey.GithubCopilotCli:
                return Split(config, carried: ["model"], flag: k => $"--{k}");

            // `codex resume` takes the thread id and little else; everything the engine sets for
            // Codex travels over the app-server protocol, which a shell command cannot express.
            case AgentToolKey.CodexCli:
            default:
                return ([], config.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList());
        }
    }

    private static (IReadOnlyList<string>, IReadOnlyList<string>) Split(
        Dictionary<string, string?> config, string[] carried, Func<string, string> flag)
    {
        var args = new List<string>();
        var dropped = new List<string>();
        foreach (var key in config.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (carried.Contains(key, StringComparer.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(config[key]))
                args.Add($"{flag(key)} {Quote(config[key]!)}");
            else
                dropped.Add(key);
        }
        return (args, dropped);
    }

    /// <summary>The permission settings, and whether the chosen start method actually applies them.</summary>
    public static void PrintPermissions(AgentToolKey tool, Profile profile, bool launching)
    {
        var permissions = profile.PermissionSettings().ToList();
        if (permissions.Count == 0)
        {
            Console.WriteLine("  permissions: (profile sets none — the CLI's own defaults apply)");
            return;
        }

        var line = $"  permissions: {string.Join("  ", permissions)}";
        if (launching)
        {
            Console.WriteLine(line);
            return;
        }

        var dropped = CommandLine(tool, profile).Dropped;
        var unapplied = permissions
            .Select(p => p[..p.IndexOf('=')])
            .Where(k => dropped.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine(line);
        if (unapplied.Count > 0)
            Console.WriteLine(
                $"               ^ NOT applied by the command below ({string.Join(", ", unapplied)}) — "
                + "use --launch, or set them in the CLI's own settings");
    }

    /// <summary>
    /// How to reopen the session in the CLI's own interface. Carries only the settings that
    /// invocation accepts — <see cref="CommandLine"/>'s <c>Dropped</c> is the rest.
    /// </summary>
    public static string ResumeCommand(AgentToolKey tool, string id, Profile profile)
    {
        var flags = CommandLine(tool, profile).Args;
        var tail = string.Concat(
            flags.Count > 0 ? " " + string.Join(' ', flags) : "",
            profile.ExtraArgs.Count > 0 ? " " + string.Join(' ', profile.ExtraArgs) : "");
        return tool switch
        {
            AgentToolKey.ClaudeCodeCli => $"claude --resume {id}{tail}",
            AgentToolKey.CodexCli => $"codex resume {id}{tail}",
            AgentToolKey.GithubCopilotCli => $"copilot --resume {id}{tail}",
            _ => $"<{tool}> resume {id}{tail}",
        };
    }

    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
