namespace Mintokei.Filesystem;

/// <summary>
/// Decides whether a filesystem-watcher event should wake the watcher (schedule
/// a flush / FileSystemChanged event). Used by the API's in-process watcher and
/// the runner's remote watcher; keeping the rule in one place keeps both sides
/// reacting to the same events — notably the appearance of a new git repo.
/// </summary>
public static class FileWatchFilter
{
    /// <summary>
    /// True when <paramref name="fullPath"/> points at a git repo root marker —
    /// a <c>.git</c> directory (normal repo) or a <c>.git</c> file (worktree /
    /// submodule). Matches the marker itself, not paths nested under it.
    /// </summary>
    public static bool IsGitRepoMarker(string fullPath)
    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(trimmed), ".git", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normal rule: react to any event that isn't under an ignored directory.
    /// Exception: even inside an ignored path, react when a <c>.git</c> marker is
    /// created, renamed, or deleted — that means a repo just appeared
    /// (<c>git init</c> / <c>clone</c> / <c>worktree add</c>) or was removed, and
    /// the git-status discovery must re-run so the right-rail Git panel reflects
    /// it without a page refresh. Plain content changes inside <c>.git/</c> (ref
    /// churn on every commit) stay ignored to avoid refetch storms.
    /// </summary>
    /// <param name="changeType">The watcher change type for the event.</param>
    /// <param name="fullPath">The event's full path.</param>
    /// <param name="isIgnored">
    /// Whether <paramref name="fullPath"/> is under an ignored directory, per the
    /// caller's ignore set (the API and runner keep their own copies).
    /// </param>
    public static bool ShouldReactToFileEvent(WatcherChangeTypes changeType, string fullPath, bool isIgnored)
        => !isIgnored || (changeType != WatcherChangeTypes.Changed && IsGitRepoMarker(fullPath));
}
