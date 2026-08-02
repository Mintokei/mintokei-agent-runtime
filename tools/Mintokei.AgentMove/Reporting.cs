using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;

namespace Mintokei.AgentMove;

/// <summary>How the conversation is picked up once it has been moved.</summary>
internal enum StartMode
{
    /// <summary>Print a command for the user to run. The default.</summary>
    PrintCommand,

    /// <summary>Run that same command here, handing this terminal to the CLI's own interface.</summary>
    Attach,

    /// <summary>Drive the CLI through the engine, in this process.</summary>
    Launch,
}

/// <summary>
/// Says truthfully which of a profile's settings will be in force, because the answer depends on
/// how the session is started.
///
/// Under <see cref="StartMode.Launch"/> the whole <c>config</c> goes to
/// <c>AgentSessionSpec.Config</c> and the backend's mapper turns it into how the CLI is run, so all
/// of it applies. The other two modes go through a command line, which can only carry what that
/// CLI's own resume invocation accepts: most of Claude's config, the model for Copilot, and —
/// because the engine drives Codex over <c>codex app-server</c> rather than by flags — nothing at
/// all for Codex.
///
/// Printing <c>approvalPolicy=on-request</c> beside a command that does not carry it is worse than
/// printing nothing: it reads as a statement about what the agent is allowed to do.
/// </summary>
internal static class Reporting
{
    /// <summary>
    /// The resume invocation for <paramref name="tool"/>: the executable, the arguments it can
    /// carry from the profile, and the config keys it cannot.
    /// </summary>
    public static (string File, IReadOnlyList<string> Argv, IReadOnlyList<string> Dropped) Resume(
        AgentToolKey tool, string id, Profile profile)
    {
        var (flags, dropped) = ConfigArgs(tool, profile);

        var argv = new List<string>();
        var file = tool switch
        {
            AgentToolKey.ClaudeCodeCli => "claude",
            AgentToolKey.CodexCli => "codex",
            AgentToolKey.GithubCopilotCli => "copilot",
            AgentToolKey.OpenCodeCli => "opencode",
            _ => tool.ToString(),
        };

        switch (tool)
        {
            // Codex's is a subcommand; the others take the id as a flag value.
            case AgentToolKey.CodexCli:
                argv.Add("resume");
                argv.Add(id);
                break;
            default:
                argv.Add("--resume");
                argv.Add(id);
                break;
        }

        argv.AddRange(flags);
        argv.AddRange(profile.ExtraArgs);
        return (file, argv, dropped);
    }

    /// <summary>The same invocation as one line, for printing.</summary>
    public static string ResumeCommand(AgentToolKey tool, string id, Profile profile)
    {
        var (file, argv, _) = Resume(tool, id, profile);
        return $"{file} {string.Join(' ', argv.Select(Quote))}";
    }

    /// <summary>The profile settings a command line can carry, and the keys it drops.</summary>
    private static (IReadOnlyList<string> Args, IReadOnlyList<string> Dropped) ConfigArgs(
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
                var args = new List<string>();
                foreach (var (flag, value) in ClaudeCodeConfigMapper.MapToCliArgs(config))
                {
                    args.Add(flag);
                    if (value is not null)
                        args.Add(value);
                }

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
                return Split(config, carried: ["model"]);

            // `codex resume` takes the thread id and little else; everything the engine sets for
            // Codex travels over the app-server protocol, which a shell command cannot express.
            case AgentToolKey.CodexCli:
            default:
                return ([], config.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList());
        }
    }

    private static (IReadOnlyList<string>, IReadOnlyList<string>) Split(
        Dictionary<string, string?> config, string[] carried)
    {
        var args = new List<string>();
        var dropped = new List<string>();
        foreach (var key in config.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (carried.Contains(key, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrEmpty(config[key]))
            {
                args.Add($"--{key}");
                args.Add(config[key]!);
            }
            else
            {
                dropped.Add(key);
            }
        }
        return (args, dropped);
    }

    /// <summary>
    /// The permission settings the profile asks for, marked with whether <paramref name="mode"/>
    /// actually delivers them. Returns the ones it does not.
    /// </summary>
    public static IReadOnlyList<string> PrintPermissions(AgentToolKey tool, Profile profile, StartMode mode)
    {
        var permissions = profile.PermissionSettings().ToList();
        if (permissions.Count == 0)
        {
            Console.WriteLine("  permissions: (profile sets none — the CLI's own defaults apply)");
            return [];
        }

        Console.WriteLine($"  permissions: {string.Join("  ", permissions)}");
        if (mode is StartMode.Launch)
            return [];

        var dropped = ConfigArgs(tool, profile).Dropped;
        var unapplied = permissions
            .Select(p => p[..p.IndexOf('=')])
            .Where(k => dropped.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unapplied.Count > 0)
            Console.WriteLine(
                $"               ^ NOT applied ({string.Join(", ", unapplied)}) — a command line "
                + "cannot carry these; --launch can");
        return unapplied;
    }

    /// <summary>Quotes an argument for display only — the spawn path passes argv unquoted.</summary>
    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
