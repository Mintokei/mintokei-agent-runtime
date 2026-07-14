using System.Diagnostics;

namespace Mintokei.Filesystem;

/// <summary>
/// Find files in a working directory whose relative path ends with a given
/// suffix. Used by the API's local handler and the runner's remote handler;
/// keeping the algorithm in one place keeps both sides ranking matches the
/// same way (more matched suffix segments first, then shallowest path).
/// </summary>
public static class FileSuffixSearch
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 50;
    public const int MaxFilesScanned = 50_000;
    public const int MaxDepth = 25;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", ".next",
        "dist", "__pycache__", ".venv", "venv", ".tox", "target", "build",
    };

    public sealed record Match(string Path, int MatchedSegments, int Depth);

    /// <summary>
    /// Scan <paramref name="workingDirectory"/> for files whose relative path
    /// ends with <paramref name="suffix"/>. Returns up to <paramref name="limit"/>
    /// matches sorted by best fit. Throws nothing — IO errors against
    /// individual subdirectories are swallowed so a single permission-denied
    /// folder doesn't tank the whole search.
    /// </summary>
    public static List<Match> Search(string workingDirectory, string suffix, int limit)
    {
        var normalized = NormalizeSuffix(suffix);
        var suffixSegments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (suffixSegments.Length == 0)
            return [];

        limit = Math.Clamp(limit, 1, MaxLimit);

        var matches = new List<Match>();
        var scanned = 0;

        var gitFiles = TryGitListFiles(workingDirectory);
        if (gitFiles is not null)
        {
            foreach (var rel in gitFiles)
            {
                if (++scanned > MaxFilesScanned) break;
                Consider(rel, suffixSegments, matches);
            }
        }
        else
        {
            WalkDirectory(workingDirectory, workingDirectory, suffixSegments, matches, ref scanned, depth: 0);
        }

        matches.Sort((a, b) =>
        {
            var bySegments = b.MatchedSegments.CompareTo(a.MatchedSegments);
            if (bySegments != 0) return bySegments;
            var byDepth = a.Depth.CompareTo(b.Depth);
            if (byDepth != 0) return byDepth;
            return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        if (matches.Count > limit)
            matches.RemoveRange(limit, matches.Count - limit);

        return matches;
    }

    private static string NormalizeSuffix(string suffix)
    {
        // Accept `/foo/bar.ts`, `foo/bar.ts`, or `\foo\bar.ts`; compare in `/`
        // form because that's what we store on relative paths regardless of
        // platform.
        return suffix.Replace('\\', '/').Trim().TrimStart('/');
    }

    private static void Consider(string relativePath, string[] suffixSegments, List<Match> matches)
    {
        var pathSegments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var matched = MatchedSuffixSegments(pathSegments, suffixSegments);
        if (matched == 0) return;
        matches.Add(new Match(
            Path: relativePath,
            MatchedSegments: matched,
            Depth: pathSegments.Length));
    }

    // Compare suffix segments against the tail of the relative path. Returns
    // the number of consecutive matched segments from the end, or 0 if the
    // last segment (the basename) doesn't match — basename mismatch means
    // it's not the file the user typed even if some interior segments
    // happen to match.
    private static int MatchedSuffixSegments(string[] pathSegments, string[] suffixSegments)
    {
        if (pathSegments.Length == 0 || suffixSegments.Length == 0) return 0;
        var compare = StringComparison.OrdinalIgnoreCase;
        var max = Math.Min(pathSegments.Length, suffixSegments.Length);
        var matched = 0;
        for (var i = 0; i < max; i++)
        {
            var a = pathSegments[pathSegments.Length - 1 - i];
            var b = suffixSegments[suffixSegments.Length - 1 - i];
            if (!string.Equals(a, b, compare)) break;
            matched++;
        }
        return matched;
    }

    private static List<string>? TryGitListFiles(string workingDir)
    {
        if (!Directory.Exists(Path.Combine(workingDir, ".git")))
            return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "ls-files --cached --others --exclude-standard",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(10));
            if (process.ExitCode != 0) return null;
            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().Replace('\\', '/'))
                .Where(l => l.Length > 0)
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    private static void WalkDirectory(
        string root, string current, string[] suffixSegments,
        List<Match> matches, ref int scanned, int depth)
    {
        if (depth > MaxDepth) return;
        if (scanned > MaxFilesScanned) return;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(current); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        foreach (var file in files)
        {
            if (++scanned > MaxFilesScanned) return;
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            Consider(rel, suffixSegments, matches);
        }

        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(current); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        foreach (var sub in subdirs)
        {
            var name = Path.GetFileName(sub);
            if (SkipDirs.Contains(name)) continue;
            WalkDirectory(root, sub, suffixSegments, matches, ref scanned, depth + 1);
        }
    }
}
