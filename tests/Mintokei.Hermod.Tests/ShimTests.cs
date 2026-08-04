using System.Diagnostics;
using System.Text.Json;

using Mintokei.Hermod;

using Xunit;

namespace Mintokei.Hermod.Tests;

/// <summary>
/// The spawn, which the argv tests cannot see.
///
/// <c>CliArgsTests.A_multiline_turn_survives_as_one_argument</c> asserts that
/// <see cref="Reporting.Resume"/> keeps the opening turn in one piece, and it does. The turn was
/// still arriving at the CLI as its first line only, because the loss happened afterwards: a batch
/// shim was run through <c>cmd.exe</c>, whose command line ends at the first newline. Every unit
/// test passed while the handoff was being thrown away.
///
/// So these tests spawn something and read back what it actually received.
/// </summary>
public class ShimTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "hermod-shim-" + Guid.NewGuid().ToString("n")[..8]);

    public ShimTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    // ── unwrapping ───────────────────────────────────────────────────────

    [Fact]
    public void An_npm_shim_resolves_to_the_script_it_wraps()
    {
        SkipUnlessWindows();
        var script = Write("codex.js", "process.exit(0)");
        // npm drops a node beside its shims and the shim prefers it, so writing one here makes the
        // test say what it means without depending on node being installed on the machine.
        var node = Write("node.exe", "");
        var shim = Write("codex.cmd", NpmShim("codex.js"));

        var unwrapped = Shims.Unwrap(shim);

        Assert.NotNull(unwrapped);
        Assert.Equal(script, unwrapped!.Value.Script, ignoreCase: true);
        Assert.Equal(node, unwrapped.Value.Interpreter, ignoreCase: true);
    }

    [Fact]
    public void A_shim_naming_a_script_that_is_not_there_is_not_unwrapped()
    {
        // The %dp0% expansion is a guess about how the shim was written. A path that does not
        // exist means the guess was wrong, and spawning it would be worse than falling back.
        SkipUnlessWindows();
        Write("node.exe", "");
        var shim = Write("codex.cmd", NpmShim("not-installed.js"));

        Assert.Null(Shims.Unwrap(shim));
    }

    [Fact]
    public void A_shim_this_does_not_understand_is_not_guessed_at()
    {
        SkipUnlessWindows();
        Write("node.exe", "");
        var shim = Write("codex.cmd", "@echo off\r\nsome-other-launcher %*\r\n");

        Assert.Null(Shims.Unwrap(shim));
    }

    // ── planning ─────────────────────────────────────────────────────────

    [Fact]
    public void A_real_executable_is_spawned_directly()
    {
        var exe = Write("codex.exe", "not really an executable");

        var (fileName, args) = Attacher.Plan(exe, ["resume", "abc"]);

        Assert.Equal(exe, fileName);
        Assert.Equal(["resume", "abc"], args);
    }

    [Fact]
    public void A_shim_it_can_read_is_stepped_over_rather_than_shelled()
    {
        SkipUnlessWindows();
        Write("codex.js", "process.exit(0)");
        var node = Write("node.exe", "");
        var shim = Write("codex.cmd", NpmShim("codex.js"));

        var (fileName, args) = Attacher.Plan(shim, ["resume", "abc"]);

        // cmd.exe is the thing being avoided: neither the interpreter nor `/c` may mention it.
        Assert.DoesNotContain("cmd.exe", fileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/c", args);
        Assert.Equal(node, fileName, ignoreCase: true);
        Assert.Equal(["resume", "abc"], args.Skip(1));
    }

    [Fact]
    public void A_multiline_turn_is_withheld_only_when_the_shell_would_cut_it()
    {
        SkipUnlessWindows();
        var unknown = Write("codex.cmd", "@echo off\r\nsome-other-launcher %*\r\n");
        Write("codex.js", "process.exit(0)");
        Write("node.exe", "");
        var npm = Write("npm-codex.cmd", NpmShim("codex.js"));
        var exe = Write("codex.exe", "not really an executable");

        string[] withTurn = ["resume", "abc", "line one\nline two"];

        Assert.True(Attacher.ShellWouldTruncate(unknown, withTurn));
        // Both of these keep the turn: one steps over the shim, the other never had a shell.
        Assert.False(Attacher.ShellWouldTruncate(npm, withTurn));
        Assert.False(Attacher.ShellWouldTruncate(exe, withTurn));
        // A single-line turn is safe even through the shell.
        Assert.False(Attacher.ShellWouldTruncate(unknown, ["resume", "abc", "one line"]));
    }

    // ── the spawn itself ─────────────────────────────────────────────────

    [Fact]
    public void The_planned_spawn_delivers_a_multiline_turn_whole()
    {
        SkipUnlessWindows();
        Assert.SkipWhen(NodePath() is null, "node is not installed here");

        var received = Path.Combine(_dir, "argv.json");
        Write("codex.js",
            "require('fs').writeFileSync(" + JsonSerializer.Serialize(received)
            + ", JSON.stringify(process.argv.slice(2)));");
        var shim = Write("codex.cmd", NpmShim("codex.js"));

        const string turn = "[handoff] first line\nsecond line\nthird line";
        var (fileName, args) = Attacher.Plan(shim, ["resume", "abc", turn]);

        Run(fileName, args);

        var argv = JsonSerializer.Deserialize<string[]>(File.ReadAllText(received))!;
        // The turn arrives as one argument with its newlines intact. Through cmd.exe this was
        // "[handoff] first line" and nothing else.
        Assert.Equal(["resume", "abc", turn], argv);
    }

    [Fact]
    public void Going_through_cmd_is_what_loses_it()
    {
        // The bug itself, pinned. If this ever stops truncating, the fallback in Plan and the
        // withholding in ShellWouldTruncate are both dead weight and can go.
        SkipUnlessWindows();
        Assert.SkipWhen(NodePath() is null, "node is not installed here");

        var received = Path.Combine(_dir, "argv-via-cmd.json");
        Write("codex.js",
            "require('fs').writeFileSync(" + JsonSerializer.Serialize(received)
            + ", JSON.stringify(process.argv.slice(2)));");
        var shim = Write("codex.cmd", NpmShim("codex.js"));

        const string turn = "[handoff] first line\nsecond line";
        Run(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/c", shim, "resume", "abc", turn]);

        var argv = JsonSerializer.Deserialize<string[]>(File.ReadAllText(received))!;
        Assert.Equal("[handoff] first line", argv[^1]);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// A batch shim is a Windows thing, and <see cref="Attacher"/> only treats one as a shim there
    /// — so everything guarded by this describes Windows behaviour and would assert the wrong thing
    /// elsewhere.
    /// </summary>
    private static void SkipUnlessWindows() =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "batch shims are a Windows problem");

    /// <summary>
    /// The shape npm generates: the script named relative to the shim's own directory, and node
    /// taken from beside it when it is there.
    /// </summary>
    private static string NpmShim(string script) => $"""
        @ECHO off
        SETLOCAL
        SET dp0=%~dp0
        IF EXIST "%dp0%\node.exe" (
          SET "_prog=%dp0%\node.exe"
        ) ELSE (
          SET "_prog=node"
        )
        "%_prog%"  "%dp0%\{script}" %*
        """;

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string? NodePath()
    {
        var resolved = AgentEngine.CommandRunner.ExecutableResolver.Resolve("node");
        return Path.IsPathRooted(resolved) && File.Exists(resolved) ? resolved : null;
    }

    private static void Run(string fileName, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "the stub did not finish");
    }
}
