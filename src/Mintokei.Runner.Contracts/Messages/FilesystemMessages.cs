namespace Mintokei.Runner.Contracts.Messages;

// --- Requests (Backend → Runner) ---

public sealed record BrowseFilesystemRequest(
    string RequestId,
    string? Path);

public sealed record DiscoverGitRepositoriesRequest(
    string RequestId,
    string Path);

// --- Responses (Runner → Backend) ---

public sealed record BrowseFilesystemResponse(
    string RequestId,
    string CurrentPath,
    string? ParentPath,
    List<FilesystemEntry> Entries,
    string? Error);

public sealed record FilesystemEntry(
    string Name,
    string FullPath,
    string Type);

public sealed record DiscoverGitRepositoriesResponse(
    string RequestId,
    List<GitRepositoryInfo> Repositories,
    string? Error);

public sealed record GitRepositoryInfo(
    string Name,
    string Branch,
    string? RemoteUrl,
    string RelativePath);

// --- Generic command execution ---

public sealed record RunCommandResponse(
    string RequestId,
    int ExitCode,
    string Stdout,
    string Stderr,
    string? Error);

// --- Directory tree ---

public sealed record DirectoryTreeNode(
    string Name,
    string Type,
    List<DirectoryTreeNode>? Children);

public sealed record GetDirectoryTreeResponse(
    string RequestId,
    List<DirectoryTreeNode>? Nodes,
    string? Error);

// --- Path resolution ---

public sealed record ResolvePathResponse(
    string RequestId,
    string ResolvedPath,
    string? Error);

// --- File content ---

public sealed record GetFileContentResponse(
    string RequestId,
    string Path,
    string? Content,
    bool IsBinary,
    bool IsTruncated,
    long FileSizeBytes,
    string? Error);

// --- File size (stat-only, no content read) ---

public sealed record GetFileSizeResponse(
    string RequestId,
    string Path,
    long? FileSizeBytes,
    string? Error);

// --- Image file (raw bytes) ---

public sealed record GetImageFileResponse(
    string RequestId,
    string Path,
    byte[]? Content,
    string? ContentType,
    long FileSizeBytes,
    string? Error);

// --- Find file by path-suffix ---

public sealed record FindFileMatchInfo(
    string Path,
    int MatchedSegments,
    int Depth);

public sealed record FindFileResponse(
    string RequestId,
    List<FindFileMatchInfo>? Matches,
    string? Error);
