# Mintokei.Filesystem

Small filesystem helpers extracted from the runner-side file services.

Most consumers do **not** reference this package directly; it exists for embedders that want the
same file matching and watcher-event filtering rules as the Mintokei runner.

## Install

```bash
dotnet add package Mintokei.Filesystem
```

## What it provides

- `FileSuffixSearch` — ranked suffix matching for file lookup inside a working directory
- `FileWatchFilter` — the runner's watcher-event filter, including the special-case `.git` marker behavior

## Example

```csharp
using Mintokei.Filesystem;

var matches = FileSuffixSearch.Search("/repo", "src/Program.cs", limit: 5);

var shouldReact = FileWatchFilter.ShouldReactToFileEvent(
    WatcherChangeTypes.Created,
    "/repo/.git",
    isIgnored: true);
```

Part of the **Mintokei Agent Runtime**.

## License

MIT
