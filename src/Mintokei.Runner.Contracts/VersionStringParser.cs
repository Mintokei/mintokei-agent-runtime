using System.Text.RegularExpressions;

namespace Mintokei.Runner.Contracts;

/// <summary>
/// Best-effort extraction of a clean version string from arbitrary CLI
/// <c>--version</c> stdout. Used as the fallback when an AgentTool has no
/// explicit <c>VersionRegex</c> configured.
///
/// Real-world inputs we want to clean up:
///   "1.0.5 (Claude Code)"                         -> "1.0.5"
///   "codex-cli 0.42.0"                            -> "0.42.0"
///   "GitHub Copilot CLI v1.4.0\nRun `copilot update`..." -> "1.4.0"
///   "claude version 1.0.5"                        -> "1.0.5"
///   "0.1.5"                                       -> "0.1.5"
/// </summary>
public static class VersionStringParser
{
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    // SemVer-ish: 1.2, 1.2.3, 1.2.3.4, with optional pre-release / build suffix
    // (-rc.1, -beta, +build.7, etc.). Leading "v" is consumed but not captured.
    private static readonly Regex SemVerLike = new(
        @"v?(\d+\.\d+(?:\.\d+){0,2}(?:[-+][0-9A-Za-z.\-]+)?)",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses <paramref name="stdout"/> into a clean version string.
    /// Falls back to the first non-empty cleaned line, then to the raw input,
    /// so callers always get something non-null/empty as long as the input was.
    /// </summary>
    /// <param name="stdout">Raw stdout from the CLI's --version invocation.</param>
    /// <param name="binaryName">
    /// The binary name (e.g. "claude") — used to strip self-referential
    /// tokens like "claude version 1.0.5" -> "1.0.5".
    /// </param>
    public static string Parse(string stdout, string? binaryName = null)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return stdout;

        var cleaned = AnsiEscape.Replace(stdout, string.Empty).Trim();
        var firstLine = FirstNonEmptyLine(cleaned);
        if (string.IsNullOrEmpty(firstLine)) return cleaned;

        var withoutBinaryName = StripBinaryName(firstLine, binaryName);

        var match = SemVerLike.Match(withoutBinaryName);
        if (match.Success) return match.Groups[1].Value;

        return withoutBinaryName.Length > 0 ? withoutBinaryName : firstLine;
    }

    private static string FirstNonEmptyLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length > 0) return line;
        }
        return string.Empty;
    }

    private static string StripBinaryName(string line, string? binaryName)
    {
        if (string.IsNullOrEmpty(binaryName)) return line;

        // Strip any token that contains the binary name, e.g. "codex-cli",
        // "claude", "GitHub Copilot CLI" -> drop tokens containing "copilot".
        // Also drop the literal word "version" / "v" that some CLIs prepend.
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            var lower = token.ToLowerInvariant();
            if (lower == "version") continue;
            if (lower.Contains(binaryName, StringComparison.OrdinalIgnoreCase)) continue;
            kept.Add(token);
        }
        return string.Join(' ', kept).Trim();
    }
}
