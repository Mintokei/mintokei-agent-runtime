using Microsoft.Extensions.Options;

namespace Mintokei.Sandbox;

/// <summary>One repo to provision inside a sandbox: its remote URL, optional base branch, and the container
/// path it lands at (defaults to <c>/repos/&lt;name-from-url&gt;</c>). A sandbox can provision several.</summary>
public sealed record SandboxRepoSpec(string Url, string? Branch = null, string? SourcePath = null);

/// <summary>Inputs describing one session's sandbox: runner identity + optional repos + optional creds.</summary>
public sealed record SandboxSessionRequest
{
    // Runner enrollment (passed as CLI flags — see note in Build).
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

/// <summary>
/// Builds a <see cref="SandboxSpec"/> for one session from the resolved profile and the session request.
/// Encodes the two things the Phase-0 prod run did by hand: repo provisioning (Step 2 env) and agent
/// credential injection (RO mounts under /seed). Runner config is passed as CLI FLAGS because
/// <c>Runner__*</c> env vars for keys present in appsettings.json are shadowed by the runner's config layering.
/// </summary>
public sealed class SandboxSpecFactory(IOptions<SandboxOptions> options)
{
    private readonly SandboxOptions _options = options.Value;

    public SandboxSpec Build(SandboxProfile profile, SandboxSessionRequest req)
    {
        var mounts = new List<SandboxMount>();
        var env = new Dictionary<string, string>();

        var args = new List<string>
        {
            "--backend", req.BackendUrl,
            "--token", req.EnrollmentToken,
            "--name", req.Name,
        };

        if (!string.IsNullOrWhiteSpace(req.GrpcBackendUrl))
            env["Runner__GrpcBackendUrl"] = req.GrpcBackendUrl!;

        if (req.Repos.Count > 0)
        {
            // Encode the repo list as ONE single-line env var (safe through the nested docker-arg dispatch,
            // which has no shell): records joined by ';', fields within a record by '|' → url|sourcePath|branch.
            // Repo URLs, /repos/<name> paths, and branch names contain none of these, so no escaping is needed.
            // prepare-workspace.sh splits on them and provisions each repo. (Legacy single-repo SANDBOX_REPO_URL
            // is still understood by the entrypoint for the spike script, but the product always uses this.)
            env["SANDBOX_REPOS"] = string.Join(';', req.Repos.Select(r =>
            {
                var src = string.IsNullOrWhiteSpace(r.SourcePath) ? DefaultSourcePath(r.Url) : r.SourcePath!;
                return $"{r.Url}|{src}|{r.Branch ?? string.Empty}";
            }));
            if (!string.IsNullOrWhiteSpace(req.RepoCacheHostPath))
                mounts.Add(new SandboxMount(req.RepoCacheHostPath!, "/repo-cache", ReadOnly: true));
        }

        if (!string.IsNullOrWhiteSpace(req.ClaudeConfigHostDir))
            mounts.Add(new SandboxMount(req.ClaudeConfigHostDir!, "/seed/.claude", ReadOnly: true));
        if (!string.IsNullOrWhiteSpace(req.ClaudeConfigJsonHostFile))
            mounts.Add(new SandboxMount(req.ClaudeConfigJsonHostFile!, "/seed/.claude.json", ReadOnly: true));
        if (!string.IsNullOrWhiteSpace(req.CodexConfigHostDir))
            mounts.Add(new SandboxMount(req.CodexConfigHostDir!, "/seed/.codex", ReadOnly: true));
        if (!string.IsNullOrWhiteSpace(req.GitCredentialsHostDir))
            mounts.Add(new SandboxMount(req.GitCredentialsHostDir!, "/seed/git", ReadOnly: true));

        return new SandboxSpec
        {
            Image = _options.Image,
            Name = req.Name,
            RuntimeClass = profile.Runtime,
            Limits = profile.Limits,
            Egress = profile.Egress,
            EgressProxyUrl = profile.EgressProxyUrl,
            Mounts = mounts,
            Env = env,
            Args = args,
            AddHostGateway = req.AddHostGateway,
        };
    }

    /// <summary>Container path every sandbox repo is provisioned under (the parent of each repo dir). The
    /// persistent-workspace volume mounts here so all of a session's repos survive a recycle together.</summary>
    public const string RepoRoot = "/repos";

    /// <summary>Derive /repos/&lt;name&gt; from a repo URL (mirrors prepare-workspace.sh's default).</summary>
    public static string DefaultSourcePath(string repoUrl)
    {
        var name = repoUrl.TrimEnd('/');
        var slash = name.LastIndexOfAny(['/', ':']);
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return $"{RepoRoot}/{name}";
    }
}
