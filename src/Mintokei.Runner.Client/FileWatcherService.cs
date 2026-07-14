using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mintokei.Runner.Filesystem;

namespace Mintokei.Runner;

/// <summary>
/// Manages file system watchers for remote workspaces.
/// Each workspace gets its own set of watchers (main dir, .git/refs, .git/HEAD)
/// with debouncing. Reports changes via a callback to be sent over SignalR.
/// </summary>
public sealed class FileWatcherService
{
    private readonly ILogger<FileWatcherService> _logger;
    private readonly ConcurrentDictionary<string, WorkspaceWatcher> _watchers = new();

    /// <summary>
    /// Called when debounced file system changes are detected for a workspace.
    /// Parameter is the workspace ID string.
    /// </summary>
    public Func<string, Task>? OnFileSystemChanged { get; set; }

    public FileWatcherService(ILogger<FileWatcherService> logger)
    {
        _logger = logger;
    }

    public void Start(string workspaceId, string path)
    {
        if (_watchers.ContainsKey(workspaceId))
        {
            _logger.LogDebug("File watcher already active for workspace {WorkspaceId}, skipping", workspaceId);
            return;
        }

        if (!Directory.Exists(path))
        {
            _logger.LogWarning("Directory {Path} does not exist, cannot start file watcher for workspace {WorkspaceId}", path, workspaceId);
            return;
        }

        var watcher = new WorkspaceWatcher(workspaceId, path, OnFlush, _logger);
        if (_watchers.TryAdd(workspaceId, watcher))
        {
            watcher.Start();
            _logger.LogInformation("File watcher started for workspace {WorkspaceId} at {Path}", workspaceId, path);
        }
        else
        {
            watcher.Dispose();
        }
    }

    public void Stop(string workspaceId)
    {
        if (_watchers.TryRemove(workspaceId, out var watcher))
        {
            watcher.Dispose();
            _logger.LogInformation("File watcher stopped for workspace {WorkspaceId}", workspaceId);
        }
    }

    public void StopAll()
    {
        foreach (var (id, watcher) in _watchers)
        {
            watcher.Dispose();
            _watchers.TryRemove(id, out _);
        }
    }

    private void OnFlush(string workspaceId)
    {
        var handler = OnFileSystemChanged;
        if (handler is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(workspaceId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error reporting file system change for workspace {WorkspaceId}", workspaceId);
                }
            });
        }
    }

    private sealed class WorkspaceWatcher : IDisposable
    {
        private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "bin", "obj", ".vs", ".idea", ".next",
            "dist", "__pycache__", ".venv", "venv", ".tox", "target", "build"
        };

        private const int DebounceMs = 800;
        private const int MaxBatchMs = 5000;

        private readonly string _workspaceId;
        private readonly string _directory;
        private readonly Action<string> _onFlush;
        private readonly ILogger _logger;

        private FileSystemWatcher? _watcher;
        private FileSystemWatcher? _gitRefsWatcher;
        private FileSystemWatcher? _gitHeadWatcher;
        private Timer? _debounceTimer;
        private DateTimeOffset _batchStartTime;
        private bool _hasPendingChanges;
        private readonly object _lock = new();
        private bool _disposed;

        public WorkspaceWatcher(
            string workspaceId,
            string directory,
            Action<string> onFlush,
            ILogger logger)
        {
            _workspaceId = workspaceId;
            _directory = directory;
            _onFlush = onFlush;
            _logger = logger;
        }

        public void Start()
        {
            CreateWatcher();
            CreateGitWatchers();
        }

        private void CreateWatcher()
        {
            var watcher = new FileSystemWatcher(_directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 65536,
                EnableRaisingEvents = true,
            };

            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnRenamedEvent;
            watcher.Error += OnWatcherError;

            _watcher = watcher;
        }

        private void CreateGitWatchers()
        {
            var gitDir = Path.Combine(_directory, ".git");
            if (!Directory.Exists(gitDir)) return;

            var refsDir = Path.Combine(gitDir, "refs");
            if (Directory.Exists(refsDir))
            {
                var refsWatcher = new FileSystemWatcher(refsDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };
                refsWatcher.Changed += OnGitRefEvent;
                refsWatcher.Created += OnGitRefEvent;
                refsWatcher.Deleted += OnGitRefEvent;
                refsWatcher.Error += OnWatcherError;
                _gitRefsWatcher = refsWatcher;
            }

            var headWatcher = new FileSystemWatcher(gitDir, "HEAD")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            headWatcher.Changed += OnGitRefEvent;
            headWatcher.Error += OnWatcherError;
            _gitHeadWatcher = headWatcher;
        }

        private void OnGitRefEvent(object sender, FileSystemEventArgs e) => ScheduleFlush();

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            if (!FileWatchFilter.ShouldReactToFileEvent(e.ChangeType, e.FullPath, ShouldIgnore(e.FullPath)))
                return;
            ScheduleFlush();
        }

        private void OnRenamedEvent(object sender, RenamedEventArgs e)
        {
            if (!FileWatchFilter.ShouldReactToFileEvent(e.ChangeType, e.FullPath, ShouldIgnore(e.FullPath))
                && !FileWatchFilter.ShouldReactToFileEvent(e.ChangeType, e.OldFullPath, ShouldIgnore(e.OldFullPath)))
                return;
            ScheduleFlush();
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            _logger.LogWarning(e.GetException(), "FileSystemWatcher error for workspace {WorkspaceId}, recreating", _workspaceId);

            DisposeWatchers();
            _onFlush(_workspaceId);

            Task.Delay(2000).ContinueWith(_ =>
            {
                if (_disposed) return;
                try
                {
                    CreateWatcher();
                    CreateGitWatchers();
                    _logger.LogInformation("FileSystemWatcher recreated for workspace {WorkspaceId}", _workspaceId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to recreate FileSystemWatcher for workspace {WorkspaceId}", _workspaceId);
                }
            });
        }

        private static bool ShouldIgnore(string fullPath)
        {
            var segments = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var segment in segments)
            {
                if (IgnoredDirectories.Contains(segment))
                    return true;
            }
            return false;
        }

        private void ScheduleFlush()
        {
            lock (_lock)
            {
                if (_disposed) return;

                var now = DateTimeOffset.UtcNow;

                if (!_hasPendingChanges)
                {
                    _batchStartTime = now;
                    _hasPendingChanges = true;
                }

                if ((now - _batchStartTime).TotalMilliseconds >= MaxBatchMs)
                {
                    FlushNow();
                    return;
                }

                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(_ =>
                {
                    lock (_lock)
                    {
                        if (_disposed) return;
                        FlushNow();
                    }
                }, null, DebounceMs, Timeout.Infinite);
            }
        }

        private void FlushNow()
        {
            _hasPendingChanges = false;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _onFlush(_workspaceId);
        }

        private void DisposeWatchers()
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            if (_gitRefsWatcher is not null)
            {
                _gitRefsWatcher.EnableRaisingEvents = false;
                _gitRefsWatcher.Dispose();
                _gitRefsWatcher = null;
            }
            if (_gitHeadWatcher is not null)
            {
                _gitHeadWatcher.EnableRaisingEvents = false;
                _gitHeadWatcher.Dispose();
                _gitHeadWatcher = null;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                DisposeWatchers();
            }
        }
    }
}
