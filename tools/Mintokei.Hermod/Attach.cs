using System.ComponentModel;
using System.Diagnostics;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.Hermod;

/// <summary>
/// Hands this terminal to the target CLI's own interface: spawns its resume command as a child
/// inheriting stdin, stdout and stderr, so the real TUI appears — colours, keybindings, slash
/// commands, all of it.
///
/// hermod sees nothing from here on: a TUI paints escape sequences meant for a human's eyes, not
/// events for a program watching. This is an <c>exec</c> with a transcript conversion in front of
/// it, which is the whole job.
/// </summary>
internal static class Attacher
{
    public static int Run(Profile profile, AgentToolKey tool, string cwd, string sessionId, string? firstTurn)
    {
        // The handoff rides along as the session's opening turn, so attaching lands in the same
        // place launching does rather than at an empty prompt with something to paste.
        var (file, argv, _) = Reporting.Resume(tool, sessionId, profile, firstTurn);
        var resolved = ExecutableResolver.Resolve(file);

        // A turn the shell would cut in half is worth more intact in front of a person than
        // truncated in front of the agent, so it comes back out of the invocation.
        var withheld = ShellWouldTruncate(resolved, argv);
        if (withheld)
        {
            (file, argv, _) = Reporting.Resume(tool, sessionId, profile, firstTurn: null);
            resolved = ExecutableResolver.Resolve(file);
        }

        var (fileName, args) = Plan(resolved, argv);

        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = cwd,
            // No redirection: the child gets this console, which is the entire point. Anything
            // redirected here would be a pipe, and the CLI would fall back to non-interactive mode.
            UseShellExecute = false,
        };

        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        if (withheld && firstTurn is { Length: > 0 })
        {
            Console.WriteLine();
            Console.WriteLine($"  `{Path.GetFileName(resolved)}` is a batch shim, and cmd.exe cannot carry a");
            Console.WriteLine("  multi-line argument — so the opening turn is not being sent. Paste it:");
            Console.WriteLine();
            foreach (var line in firstTurn.ReplaceLineEndings("\n").Split('\n'))
                Console.WriteLine($"    {line}");
            Console.WriteLine();
        }

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

    /// <summary>
    /// What to spawn and with which arguments.
    ///
    /// On Windows these CLIs are usually npm shims — `codex.cmd`, not `codex.exe`. CreateProcess
    /// resolves a bare name against PATH but only ever appends `.exe`, and a `.cmd` is not an
    /// executable image at all, so it cannot simply be started. Going through `cmd.exe` is the
    /// obvious answer and the wrong one: see <see cref="Shims"/> for what it costs. Stepping over
    /// the shim to the interpreter it wraps keeps the shell out of the spawn entirely, and is tried
    /// first for that reason.
    /// </summary>
    internal static (string FileName, IReadOnlyList<string> Args) Plan(
        string resolved, IReadOnlyList<string> argv)
    {
        var args = new List<string>();

        if (!IsBatch(resolved))
        {
            args.AddRange(argv);
            return (resolved, args);
        }

        if (Shims.Unwrap(resolved) is { } shim)
        {
            args.Add(shim.Script);
            args.AddRange(argv);
            return (shim.Interpreter, args);
        }

        // An unrecognised shim still has to run, and every argument left by the time we get here is
        // one the shell can carry — ShellWouldTruncate took the rest out.
        args.Add("/c");
        args.Add(resolved);
        args.AddRange(argv);
        return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", args);
    }

    /// <summary>
    /// Whether spawning this would hand <c>cmd.exe</c> an argument it cannot carry whole. Only the
    /// newline is treated as fatal: it is the one character no escape survives, and it is the one
    /// the handoff always contains.
    /// </summary>
    internal static bool ShellWouldTruncate(string resolved, IReadOnlyList<string> argv) =>
        IsBatch(resolved)
        && Shims.Unwrap(resolved) is null
        && argv.Any(a => a.Contains('\n') || a.Contains('\r'));

    private static bool IsBatch(string resolved) =>
        OperatingSystem.IsWindows()
        && (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
}
