using System.ComponentModel;
using System.Diagnostics;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.AgentMove;

/// <summary>
/// Hands this terminal to the target CLI's own interface: spawns its resume command as a child
/// inheriting stdin, stdout and stderr, so the real TUI appears — colours, keybindings, slash
/// commands, all of it.
///
/// The trade against <see cref="Launcher"/> is total. A TUI paints with escape sequences meant for
/// a human's eyes, so from here agentmove can see nothing: no permission to intercept, no rate
/// limit to notice, no second move to make. It is an <c>exec</c> with a transcript conversion in
/// front of it — which, most of the time, is exactly what you want.
/// </summary>
internal static class Attacher
{
    public static int Run(Profile profile, AgentToolKey tool, string cwd, string sessionId, string? firstTurn)
    {
        // The handoff rides along as the session's opening turn, so attaching lands in the same
        // place launching does rather than at an empty prompt with something to paste.
        var (file, argv, _) = Reporting.Resume(tool, sessionId, profile, firstTurn);

        // On Windows these CLIs are usually npm shims — `codex.cmd`, not `codex.exe`. CreateProcess
        // resolves a bare name against PATH but only ever appends `.exe`, so without this the
        // spawn fails with "cannot find the file"; and a `.cmd` is not an executable image at all,
        // so even the resolved path has to go through the command interpreter.
        var resolved = ExecutableResolver.Resolve(file);
        var isBatch = OperatingSystem.IsWindows()
            && (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        var start = new ProcessStartInfo
        {
            FileName = isBatch ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : resolved,
            WorkingDirectory = cwd,
            // No redirection: the child gets this console, which is the entire point. Anything
            // redirected here would be a pipe, and the CLI would fall back to non-interactive mode.
            UseShellExecute = false,
        };

        if (isBatch)
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(resolved);
        }
        foreach (var arg in argv)
            start.ArgumentList.Add(arg);

        Process? child;
        try
        {
            child = Process.Start(start);
        }
        catch (Win32Exception ex)
        {
            Console.Error.WriteLine($"could not run `{file}`: {ex.Message}");
            Console.Error.WriteLine($"the conversation is still there — {Reporting.ResumeCommand(tool, sessionId, profile)}");
            return 1;
        }

        if (child is null)
        {
            Console.Error.WriteLine($"could not run `{file}`");
            return 1;
        }

        // Ctrl-C reaches the whole foreground process group, so the CLI already gets its own. This
        // handler exists only to stop the default one from tearing this process down first and
        // leaving the child orphaned on a terminal nobody is reading.
        ConsoleCancelEventHandler ignore = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += ignore;
        try
        {
            child.WaitForExit();
            return child.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= ignore;
            child.Dispose();
        }
    }
}
