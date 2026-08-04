using System.Diagnostics;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.Hermod;

using Xunit;

namespace Mintokei.Hermod.Tests;

/// <summary>
/// The layer no unit test can reach: whether the flags <see cref="CliArgs"/> emits exist in the CLI
/// that will receive them.
///
/// This is not hypothetical. The engine turned <c>noProjectDoc</c> into <c>--no-project-doc</c>, a
/// flag <c>codex app-server</c> does not have, and every caller got a process that died at launch:
///
/// <code>error: unexpected argument '--no-project-doc' found</code>
///
/// Both other test files pass with that bug present — the mapper consumes the key, and the table
/// matches the mapper. Only the CLI itself knows.
///
/// The CLI is asked by <em>invocation</em>, not by reading <c>--help</c>: the first version of this
/// test grepped the help text and failed on Claude's <c>--max-turns</c>, which is real but
/// undocumented there. So the real invocation is built against a session id that cannot exist —
/// argument parsing happens first, and "no such session" means every flag before it was accepted.
/// Nothing is launched and no turn is taken.
///
/// Each test skips when its CLI is absent, so CI without the tooling stays green while a machine
/// with it gets the check.
/// </summary>
public class InstalledCliTests
{
    /// <summary>A well-formed id that will not match a stored session.</summary>
    private const string NoSuchSession = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// What each CLI says about an argument it does not know. Claude and Copilot are commander,
    /// Codex is clap; anything else in the output is some later failure, which is what we want.
    /// </summary>
    private static readonly string[] RejectionSignatures =
    [
        "unknown option",       // commander
        "unexpected argument",  // clap
        "unrecognized option",
        "unrecognised option",
    ];

    [Fact]
    public void Codex_accepts_every_flag_we_would_send_it()
        => AssertAccepted(AgentToolKey.CodexCli, "codex",
            [("sandbox", "read-only"), ("approvalPolicy", "on-request"), ("model", "gpt-5.5"),
             ("webSearch", "true"), ("effort", "low"), ("noProjectDoc", "true"),
             ("summary", "none"), ("modelVerbosity", "low"), ("personality", "concise")]);

    [Fact]
    public void Claude_accepts_every_flag_we_would_send_it()
        => AssertAccepted(AgentToolKey.ClaudeCodeCli, "claude",
            [("model", "claude-sonnet-4-5"), ("permissionMode", "acceptEdits"), ("effort", "low"),
             ("maxTurns", "5"), ("allowedTools", "Read"), ("verbose", "true")]);

    [Fact]
    public void Copilot_accepts_every_flag_we_would_send_it()
        => AssertAccepted(AgentToolKey.GithubCopilotCli, "copilot",
            [("model", "gpt-5.5"), ("mode", "interactive"), ("effort", "low"),
             ("allowAllPaths", "true"), ("disableAskUser", "true"), ("disableBuiltinMcps", "true"),
             ("maxAutopilotContinues", "3")]);

    [Fact]
    public void A_flag_that_does_not_exist_is_detected()
    {
        // Guards the guard: if a CLI stopped announcing rejections in a way this recognises, every
        // test above would pass regardless of what it was sent.
        var output = TryRun("codex", ["resume", NoSuchSession, "--definitely-not-a-flag"]);
        Assert.SkipWhen(output is null, "codex is not installed here");
        Assert.True(WasRejected(output!), $"expected an argument rejection, got: {Trim(output!)}");
    }

    private static void AssertAccepted(
        AgentToolKey tool, string executable, (string Key, string Value)[] config)
    {
        var profile = new Profile { Tool = executable };
        foreach (var (key, value) in config)
            profile.Config[key] = value;

        var (file, argv, _) = Reporting.Resume(tool, NoSuchSession, profile);
        var output = TryRun(file, argv);
        Assert.SkipWhen(output is null, $"{executable} is not installed here");

        Assert.False(WasRejected(output!),
            $"{executable} rejected the invocation hermod builds:\n"
            + $"  {file} {string.Join(' ', argv)}\n"
            + $"  {Trim(output!)}");
    }

    private static bool WasRejected(string output) =>
        RejectionSignatures.Any(s => output.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static string Trim(string output) =>
        output.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim()
        ?? "(no output)";

    /// <summary>The CLI's combined output, or null when it is not installed or would not finish.</summary>
    private static string? TryRun(string executable, IReadOnlyList<string> argv)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,   // never a terminal: no TUI can take hold
                UseShellExecute = false,
            };
            foreach (var arg in argv)
                start.ArgumentList.Add(arg);

            using var process = Process.Start(start);
            if (process is null)
                return null;

            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return stdout.Result + stderr.Result;
        }
        catch (Exception)
        {
            // Not installed, not on PATH, or not runnable here. Not this test's business.
            return null;
        }
    }
}
