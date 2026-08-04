using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.Hermod;

/// <summary>
/// Finds the interpreter and script behind a Windows batch shim, so the shim can be stepped over
/// rather than run through <c>cmd.exe</c>.
///
/// This exists because of what <c>cmd.exe</c> does to the opening turn. Its command line ends at
/// the first newline — no quoting or <c>^</c> escape preserves one, the latter deletes it — so a
/// multi-line handoff arrives as its first line and the rest is gone. Worse, the cut lands inside
/// the opening quote, and the unterminated string then swallows whatever the shim does next.
///
/// npm installs these CLIs as <c>codex.cmd</c>, not <c>codex.exe</c>, so on the setup the comment
/// in <see cref="Attacher"/> calls usual, that is the whole handoff lost without a word. The shim
/// is only a wrapper around <c>node script.js %*</c>: spawning that directly takes the shell out
/// of the picture, which fixes the newline and every <c>cmd.exe</c> metacharacter with it.
/// </summary>
internal static class Shims
{
    /// <summary>
    /// The interpreter and script <paramref name="shimPath"/> wraps, or null when it does not look
    /// like one this understands — pnpm, yarn and bun all generate different files, and a wrong
    /// guess here would spawn the wrong process. The caller falls back to the shell.
    /// </summary>
    public static (string Interpreter, string Script)? Unwrap(string shimPath)
    {
        string text;
        string directory;
        try
        {
            var full = Path.GetFullPath(shimPath);
            directory = Path.GetDirectoryName(full) ?? "";
            text = File.ReadAllText(full);
        }
        catch (Exception)
        {
            return null;   // unreadable, gone, or not a path: nothing to unwrap
        }

        foreach (var quoted in QuotedTokens(text))
        {
            var script = Locate(quoted, directory);
            if (script is null)
                continue;

            // Only ever node: the extension is what identified the script in the first place, and
            // guessing an interpreter for anything else would be inventing one.
            var interpreter = FindNode(directory);
            return interpreter is null ? null : (interpreter, script);
        }

        return null;
    }

    /// <summary>
    /// The script that <paramref name="token"/> names, or null when it names something else.
    /// Existence is checked rather than assumed — <c>%dp0%</c> expansion is a guess about how the
    /// shim was written, and a path that is not there means the guess was wrong.
    /// </summary>
    private static string? Locate(string token, string directory)
    {
        if (!IsScript(token))
            return null;

        // Both spellings appear: `%~dp0` where the shim expands it inline, `%dp0%` where it was
        // captured into a variable first. Each already ends in a separator.
        var expanded = token
            .Replace("%~dp0", directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("%dp0%", directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        // Anything still holding a %VAR% is a variable this does not know how to expand.
        if (expanded.Contains('%'))
            return null;

        try
        {
            var full = Path.GetFullPath(Path.Combine(directory, expanded));
            return File.Exists(full) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsScript(string token) =>
        token.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || token.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)
        || token.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// node, preferring the copy npm drops beside its shims over whatever PATH finds. Null when
    /// there is none, because spawning a name that does not resolve just moves the failure.
    /// </summary>
    private static string? FindNode(string directory)
    {
        var beside = Path.Combine(directory, OperatingSystem.IsWindows() ? "node.exe" : "node");
        if (File.Exists(beside))
            return beside;

        // Resolve hands the name back unchanged when `where` finds nothing, so a bare name here
        // means it is not installed.
        var resolved = ExecutableResolver.Resolve("node");
        return Path.IsPathRooted(resolved) && File.Exists(resolved) ? resolved : null;
    }

    /// <summary>The double-quoted runs in <paramref name="text"/>, in order.</summary>
    private static IEnumerable<string> QuotedTokens(string text)
    {
        var parts = text.Split('"');
        // Splitting on the delimiter puts what was inside the quotes at every odd index.
        for (var i = 1; i < parts.Length; i += 2)
            yield return parts[i];
    }
}
