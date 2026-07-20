namespace Mintokei.Sandbox;

/// <summary>One repo to provision inside a sandbox: its remote URL, optional base branch, and the container
/// path it lands at (defaults to <c>/repos/&lt;name-from-url&gt;</c>). A sandbox can provision several.</summary>
public sealed record SandboxRepoSpec(string Url, string? Branch = null, string? SourcePath = null);

/// <summary>
/// Inputs describing one session's sandbox: runner identity + optional repos + optional creds. The embedder
/// supplies this through <see cref="ISandboxSessionSource"/>; <see cref="SandboxSpecFactory"/> turns it into
/// the <see cref="SandboxSpec"/> that an <see cref="ISandboxRuntime"/> launches.
/// </summary>
public sealed record SandboxSessionRequest
{
    // Runner enrollment (passed as CLI flags — see the note in SandboxSpecFactory.Build).
    public required string BackendUrl { get; init; }
    public required string EnrollmentToken { get; init; }
    public required string Name { get; init; }
    public string? GrpcBackendUrl { get; init; }              // dev only (no flag; absent from appsettings so env binds)
    public bool AddHostGateway { get; init; }                 // dev only

    // Step 2 repo provisioning (git alternates), read by the container entrypoint's prepare-workspace.
    // One or more repos, each cloned into SandboxSpecFactory.RepoRoot/<name> in the container (borrowing
    // objects from the RO mirror when present). Empty for repo-agnostic warm sandboxes.
    public IReadOnlyList<SandboxRepoSpec> Repos { get; init; } = [];
    public string? RepoCacheHostPath { get; init; }           // per-host bare mirror, mounted RO at /repo-cache

    // Credential seeding: mounted RO at /seed; the entrypoint copies into a writable HOME.
    public string? ClaudeConfigHostDir { get; init; }         // host ~/.claude
    public string? ClaudeConfigJsonHostFile { get; init; }    // host ~/.claude.json
    public string? CodexConfigHostDir { get; init; }          // host ~/.codex

    // Git credentials for cloning a private repo over the network (mounted RO at /seed/git; the
    // entrypoint seeds ~/.git-credentials + ~/.ssh from it before prepare-workspace's clone). Point at a
    // host dir holding .git-credentials (+ optional .ssh/). Not needed when SANDBOX_REPO_URL is public or
    // served from a local RO mirror (RepoCacheHostPath).
    public string? GitCredentialsHostDir { get; init; }
}
