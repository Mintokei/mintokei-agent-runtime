using Microsoft.Extensions.Options;

namespace Mintokei.Sandbox;

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
