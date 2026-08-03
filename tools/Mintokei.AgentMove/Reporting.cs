using Mintokei.AgentEngine.AgentTools;

namespace Mintokei.AgentMove;

/// <summary>
/// Says truthfully which of a profile's settings will be in force.
///
/// Everything reaches the CLI as an argument to its own resume invocation — agentmove either prints
/// that command or runs it. A key <see cref="CliArgs"/> cannot express is refused up front rather
/// than reported here, so in practice this prints the profile and nothing else; the unapplied path
/// remains because a backend can gain a key before it gains a flag for it.
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
    /// <remarks>
    /// <paramref name="firstTurn"/> is sent as soon as the session opens. All three CLIs take one —
    /// Claude and Codex as a trailing positional, Copilot behind <c>-i</c> — which is what lets
    /// <c>--attach</c> deliver the handoff itself rather than asking for it to be pasted.
    /// </remarks>
    public static (string File, IReadOnlyList<string> Argv, IReadOnlyList<string> Dropped) Resume(
        AgentToolKey tool, string id, Profile profile, string? firstTurn = null)
    {
        var (flags, dropped) = CliArgs.For(tool, profile);

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

        if (!string.IsNullOrWhiteSpace(firstTurn))
        {
            // Copilot needs the flag; Claude and Codex take a bare positional, which must come
            // last so it is not read as the value of whatever preceded it.
            if (tool is AgentToolKey.GithubCopilotCli)
                argv.Add("-i");
            argv.Add(firstTurn);
        }

        return (file, argv, dropped);
    }

    /// <summary>The same invocation as one line, for printing.</summary>
    public static string ResumeCommand(AgentToolKey tool, string id, Profile profile) =>
        Render(Resume(tool, id, profile));

    private static string Render((string File, IReadOnlyList<string> Argv, IReadOnlyList<string> _) resume) =>
        $"{resume.File} {string.Join(' ', resume.Argv.Select(Quote))}";

    /// <summary>
    /// The permission settings the profile asks for, marked with any the command line cannot
    /// deliver. Returns the ones it cannot.
    /// </summary>
    public static IReadOnlyList<string> PrintPermissions(AgentToolKey tool, Profile profile)
    {
        var permissions = profile.PermissionSettings().ToList();
        if (permissions.Count == 0)
        {
            Console.WriteLine("  permissions: (profile sets none — the CLI's own defaults apply)");
            return [];
        }

        Console.WriteLine($"  permissions: {string.Join("  ", permissions)}");

        var dropped = CliArgs.For(tool, profile).Dropped;
        var unapplied = permissions
            .Select(p => p[..p.IndexOf('=')])
            .Where(k => dropped.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unapplied.Count > 0)
            Console.WriteLine(
                $"               ^ NOT applied ({string.Join(", ", unapplied)}) — {tool} has no "
                + "flag for these; set them in its own settings instead");
        return unapplied;
    }

    /// <summary>Quotes an argument for display only — the spawn path passes argv unquoted.</summary>
    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
