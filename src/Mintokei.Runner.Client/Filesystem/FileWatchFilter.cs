namespace Mintokei.Runner.Filesystem;

/// <summary>
/// Decides whether a filesystem-watcher event should wake the runner watcher.
/// </summary>
internal static class FileWatchFilter
{
    public static bool IsGitRepoMarker(string fullPath)
    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(trimmed), ".git", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldReactToFileEvent(WatcherChangeTypes changeType, string fullPath, bool isIgnored)
        => !isIgnored || (changeType != WatcherChangeTypes.Changed && IsGitRepoMarker(fullPath));
}
