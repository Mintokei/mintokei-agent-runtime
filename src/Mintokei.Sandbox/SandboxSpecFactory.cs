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

        // Broker egress is the hardened posture: no long-lived secret is seeded into the box (the broker injects
        // short-lived, scoped creds), so egress must be genuinely bounded. Fail closed on a misconfiguration
        // rather than silently launching an unbounded or credential-less sandbox.
        var brokered = profile.Egress == SandboxEgress.Broker;
        if (brokered)
        {
            if (profile.EgressAllowlist.Count == 0)
                throw new SandboxRuntimeException(
                    $"profile '{profile.Name}' uses broker egress but its EgressAllowlist is empty — refusing to launch (fail-closed).");
            if (req.AddHostGateway)
                throw new SandboxRuntimeException(
                    $"profile '{profile.Name}' uses broker egress, which is incompatible with AddHostGateway (host reachability defeats containment).");

            // The in-sandbox runner reaches the control plane through the broker's CONNECT proxy, and .NET's
            // SocketsHttpHandler only CONNECT-tunnels TLS — a plaintext http:// h2c gRPC URL would bypass the
            // proxy (and, on a deny-by-default network, simply never connect). Require an https endpoint so the
            // dial-back actually traverses the broker instead of the sandbox silently failing to enrol.
            var grpcUrl = string.IsNullOrWhiteSpace(req.GrpcBackendUrl) ? req.BackendUrl : req.GrpcBackendUrl!;
            if (!grpcUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new SandboxRuntimeException(
                    $"profile '{profile.Name}' uses broker egress, which routes the runner's gRPC dial-back through a " +
                    $"CONNECT proxy that only tunnels TLS — set an https:// GrpcBackendUrl/BackendUrl (got '{grpcUrl}').");
        }

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

        // Credential seeding (RO mounts under /seed) — SKIPPED in broker mode, whose whole point is that no
        // long-lived secret ever enters the container; the broker injects short-lived, scoped creds instead.
        if (!brokered)
        {
            if (!string.IsNullOrWhiteSpace(req.ClaudeConfigHostDir))
                mounts.Add(new SandboxMount(req.ClaudeConfigHostDir!, "/seed/.claude", ReadOnly: true));
            if (!string.IsNullOrWhiteSpace(req.ClaudeConfigJsonHostFile))
                mounts.Add(new SandboxMount(req.ClaudeConfigJsonHostFile!, "/seed/.claude.json", ReadOnly: true));
            if (!string.IsNullOrWhiteSpace(req.CodexConfigHostDir))
                mounts.Add(new SandboxMount(req.CodexConfigHostDir!, "/seed/.codex", ReadOnly: true));
            if (!string.IsNullOrWhiteSpace(req.GitCredentialsHostDir))
                mounts.Add(new SandboxMount(req.GitCredentialsHostDir!, "/seed/git", ReadOnly: true));
        }

        // Under a read-only rootfs the paths the runner + agent CLIs must write to (data dir, HOME, /tmp, and
        // the repos root) have to be writable tmpfs. The repos root also appears as a persisted volume mount when
        // enabled — the backends drop the tmpfs for any path that is also a mount, so the volume wins there.
        IReadOnlyList<string> tmpfs = profile.ReadOnlyRootfs
            ? ["/data", SandboxImage.AgentHome, "/tmp", RepoRoot]
            : ["/data"];

        return new SandboxSpec
        {
            Image = _options.Image,
            Name = req.Name,
            RuntimeClass = profile.Runtime,
            Limits = profile.Limits,
            Egress = profile.Egress,
            EgressProxyUrl = profile.EgressProxyUrl,
            EgressAllowlist = profile.EgressAllowlist,
            Mounts = mounts,
            Env = env,
            Args = args,
            Tmpfs = tmpfs,
            ReadOnlyRootfs = profile.ReadOnlyRootfs,
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
