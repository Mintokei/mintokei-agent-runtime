using Microsoft.Extensions.Options;

namespace Mintokei.Sandbox;

/// <summary>Inputs describing one session's sandbox: runner identity + optional repo + optional creds.</summary>
public sealed record SandboxSessionRequest
{
    // Runner enrollment (passed as CLI flags — see note in Build).
    public required string BackendUrl { get; init; }
    public required string EnrollmentToken { get; init; }
    public required string Name { get; init; }
    public string? GrpcBackendUrl { get; init; }              // dev only (no flag; absent from appsettings so env binds)
    public bool AddHostGateway { get; init; }                 // dev only

    // Step 2 repo provisioning (git alternates), read by the container entrypoint's prepare-workspace.
    public string? RepoUrl { get; init; }
    public string? RepoBranch { get; init; }
    public string? SourcePath { get; init; }                  // defaults to /repos/<name-from-url>
    public string? RepoCacheHostPath { get; init; }           // per-host bare mirror, mounted RO at /repo-cache

    // Credential seeding: mounted RO at /seed; the entrypoint copies into a writable HOME.
    public string? ClaudeConfigHostDir { get; init; }         // host ~/.claude
    public string? ClaudeConfigJsonHostFile { get; init; }    // host ~/.claude.json
    public string? CodexConfigHostDir { get; init; }          // host ~/.codex
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

        if (!string.IsNullOrWhiteSpace(req.RepoUrl))
        {
            env["SANDBOX_REPO_URL"] = req.RepoUrl!;
            env["SANDBOX_SOURCE_PATH"] = string.IsNullOrWhiteSpace(req.SourcePath)
                ? DefaultSourcePath(req.RepoUrl!)
                : req.SourcePath!;
            if (!string.IsNullOrWhiteSpace(req.RepoBranch))
                env["SANDBOX_REPO_BRANCH"] = req.RepoBranch!;
            if (!string.IsNullOrWhiteSpace(req.RepoCacheHostPath))
                mounts.Add(new SandboxMount(req.RepoCacheHostPath!, "/repo-cache", ReadOnly: true));
        }

        if (!string.IsNullOrWhiteSpace(req.ClaudeConfigHostDir))
            mounts.Add(new SandboxMount(req.ClaudeConfigHostDir!, "/seed/.claude", ReadOnly: true));
        if (!string.IsNullOrWhiteSpace(req.ClaudeConfigJsonHostFile))
            mounts.Add(new SandboxMount(req.ClaudeConfigJsonHostFile!, "/seed/.claude.json", ReadOnly: true));
        if (!string.IsNullOrWhiteSpace(req.CodexConfigHostDir))
            mounts.Add(new SandboxMount(req.CodexConfigHostDir!, "/seed/.codex", ReadOnly: true));

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

    /// <summary>Derive /repos/&lt;name&gt; from a repo URL (mirrors prepare-workspace.sh's default).</summary>
    public static string DefaultSourcePath(string repoUrl)
    {
        var name = repoUrl.TrimEnd('/');
        var slash = name.LastIndexOfAny(['/', ':']);
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return $"/repos/{name}";
    }
}
