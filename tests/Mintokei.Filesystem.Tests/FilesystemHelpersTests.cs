using Mintokei.Filesystem;
using Xunit;

namespace Mintokei.Filesystem.Tests;

public sealed class FilesystemHelpersTests
{
    [Fact]
    public void Search_RanksMoreSpecificSuffixAheadOfBasenameOnlyMatch()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("Program.cs", "// root");
        temp.WriteFile(Path.Combine("src", "Program.cs"), "// src");

        var matches = FileSuffixSearch.Search(temp.Path, "src/Program.cs", limit: 10);

        Assert.Equal(2, matches.Count);
        Assert.Equal("src/Program.cs", matches[0].Path);
        Assert.Equal(2, matches[0].MatchedSegments);
        Assert.Equal("Program.cs", matches[1].Path);
        Assert.Equal(1, matches[1].MatchedSegments);
    }

    [Fact]
    public void Search_SkipsIgnoredBuildAndDependencyDirectories()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("src", "Program.cs"), "// src");
        temp.WriteFile(Path.Combine("node_modules", "Program.cs"), "// ignored");
        temp.WriteFile(Path.Combine("bin", "Program.cs"), "// ignored");

        var matches = FileSuffixSearch.Search(temp.Path, "Program.cs", limit: 10);

        var returnedPaths = matches.Select(m => m.Path).ToList();
        Assert.Equal(["src/Program.cs"], returnedPaths);
    }

    [Fact]
    public void FileWatchFilter_ReactsToGitMarkerButNotOrdinaryIgnoredChanges()
    {
        Assert.False(FileWatchFilter.ShouldReactToFileEvent(
            WatcherChangeTypes.Changed,
            "/repo/node_modules/package.json",
            isIgnored: true));

        Assert.True(FileWatchFilter.ShouldReactToFileEvent(
            WatcherChangeTypes.Created,
            "/repo/.git",
            isIgnored: true));

        Assert.False(FileWatchFilter.ShouldReactToFileEvent(
            WatcherChangeTypes.Changed,
            "/repo/.git",
            isIgnored: true));

        Assert.True(FileWatchFilter.ShouldReactToFileEvent(
            WatcherChangeTypes.Changed,
            "/repo/src/Program.cs",
            isIgnored: false));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mintokei-filesystem-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void WriteFile(string relativePath, string content)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
