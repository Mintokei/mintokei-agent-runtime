namespace Mintokei.Sandbox;

/// <summary>Which credential source is being staged — decides what is safe to leave behind.</summary>
public enum SandboxCredentialKind
{
    /// <summary>An agent CLI home (<c>~/.claude</c>): a few KB of credentials and config buried in what can be
    /// GBs of plugin marketplaces, session transcripts and caches.</summary>
    ClaudeHome,

    /// <summary>The single <c>~/.claude.json</c> file.</summary>
    ClaudeJson,

    /// <summary>A Codex CLI home (<c>~/.codex</c>) — same shape as <see cref="ClaudeHome"/>.</summary>
    CodexHome,

    /// <summary>A git credential directory. Small by nature (a token file, maybe an ssh key), so it is copied
    /// whole — there is nothing in it worth the risk of a partial copy.</summary>
    GitCreds,

    /// <summary>Anything else: copied whole, since we can't know what is safe to drop.</summary>
    Unknown,
}

/// <summary>How much of a source to take.</summary>
public enum SandboxStagingScope
{
    /// <summary>Everything the agent CLI needs to run — its config and credentials, minus the runner's
    /// accumulated history and caches.</summary>
    AgentHome,

    /// <summary>ONLY the credential files a broker's <c>${json:}</c> / <c>${gitcreds:}</c> refs actually read.
    /// The broker runs no agent CLI, so it needs no config at all.</summary>
    BrokerSecrets,
}

/// <summary>
/// The ONE definition of what "staging credentials" copies, shared by every site that stages them: the remote
/// worker path (<see cref="SandboxCredentialStager"/>, run as a shell script over the runner link) and the two
/// Kubernetes init containers (the sandbox pod's and the broker pod's).
///
/// It lives in one place because it did not, and that cost. The remote path learned to trim an agent home
/// after a GB-scale <c>~/.claude</c> blew the staging timeout and failed provisioning outright; both Kubernetes
/// init containers kept doing a wholesale <c>cp -aL</c>. The result was invisible — nothing errored — but every
/// K8s sandbox spent ~35s copying ~1.1GB into a tmpfs <c>emptyDir</c> before its pod could start, which is
/// memory as well as latency. The broker's share of that delivered exactly 583 bytes it actually reads.
///
/// Both mechanisms end up running POSIX <c>sh</c>, so what is shared here is the script itself, not just a
/// list of names: a caller emits <see cref="ShellFunctions"/> once and then one <see cref="CopyCommand"/> per
/// source. A new staging site inherits the policy by construction rather than by remembering to.
/// </summary>
public static class SandboxCredentialStaging
{
    /// <summary>Subtrees of an agent CLI home that are cache/history, never credentials. Dropping them is what
    /// turns a GB-scale copy into a few hundred KB.</summary>
    public static readonly string[] ClaudeHomeExcludes =
        ["plugins", "projects", "shell-snapshots", "file-history", "cache", "backups", "todos", "history.jsonl"];

    /// <inheritdoc cref="ClaudeHomeExcludes"/>
    public static readonly string[] CodexHomeExcludes = ["sessions", "plugins", "cache"];

    /// <summary>The only files a broker reads out of an agent home — see <c>SandboxBrokerSecrets</c>, whose
    /// upstream refs name exactly these. An allow-list, not an exclude-list: for the broker we know precisely
    /// what is needed, so anything new that appears in the home is excluded by default rather than by update.
    /// </summary>
    public static readonly string[] ClaudeBrokerSecrets = [".credentials.json"];

    /// <inheritdoc cref="ClaudeBrokerSecrets"/>
    public static readonly string[] CodexBrokerSecrets = ["auth.json"];

    /// <summary>
    /// POSIX sh helpers every staging site emits once, before its <see cref="CopyCommand"/> calls.
    ///
    /// <c>cptrim</c> copies a directory minus named subtrees; <c>tar -h</c> dereferences symlinks (like
    /// <c>cp -L</c>) and tolerates a transient or broken file instead of aborting the whole stage.
    /// <c>cpick</c> takes only named entries. Both are best-effort per entry and never fail the script — a
    /// missing optional credential must not break provisioning, and callers run under <c>set -e</c>.
    /// </summary>
    public const string ShellFunctions = """
        cptrim() {
          s=$1; d=$2; shift 2
          mkdir -p "$d"
          x=""
          for e in "$@"; do x="$x --exclude=./$e"; done
          ( cd "$s" && tar -chf - $x . 2>/dev/null ) | ( cd "$d" && tar -xf - 2>/dev/null ) || true
        }
        cpick() {
          s=$1; d=$2; shift 2
          mkdir -p "$d"
          for f in "$@"; do
            if [ -e "$s/$f" ]; then cp -aL "$s/$f" "$d/$f" 2>/dev/null || true; fi
          done
        }
        """;

    /// <summary>
    /// Classify a source by the name it is mounted as (<c>.claude</c>, <c>.claude.json</c>, <c>.codex</c>,
    /// <c>git</c>). Matching is on the leaf name so it works for both a mount target and a host path.
    /// Anything unrecognised is <see cref="SandboxCredentialKind.Unknown"/> and copied whole — the safe
    /// default, since trimming something we don't understand could silently drop a credential.
    /// </summary>
    public static SandboxCredentialKind KindFor(string nameOrPath)
    {
        var leaf = nameOrPath.TrimEnd('/', '\\');
        var slash = leaf.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) leaf = leaf[(slash + 1)..];

        // Both spellings: the seed mounts use the CLI's own dot-names (".claude"), the broker mounts use bare
        // names under /creds ("claude"). Same source, so they must classify the same — this is exactly the kind
        // of split that let the two paths drift apart in the first place.
        return leaf switch
        {
            ".claude" or "claude" => SandboxCredentialKind.ClaudeHome,
            ".claude.json" => SandboxCredentialKind.ClaudeJson,
            ".codex" or "codex" => SandboxCredentialKind.CodexHome,
            "git" or ".git-credentials" => SandboxCredentialKind.GitCreds,
            _ => SandboxCredentialKind.Unknown,
        };
    }

    /// <summary>
    /// The shell command that stages one source. <paramref name="src"/> and <paramref name="dst"/> are emitted
    /// verbatim, so a caller may pass a literal path or a shell variable (<c>"$2"</c>) — both staging sites do
    /// one or the other. Assumes <see cref="ShellFunctions"/> is already in scope.
    /// </summary>
    public static string CopyCommand(string src, string dst, SandboxCredentialKind kind, SandboxStagingScope scope)
        => (kind, scope) switch
        {
            // A single file: nothing to trim either way.
            (SandboxCredentialKind.ClaudeJson, _) => $"cp -aL {src} {dst} 2>/dev/null || true",

            // Broker: take only the files its refs read. Everything else in the home — including the
            // conversation transcripts under projects/ — has no business being staged into it.
            (SandboxCredentialKind.ClaudeHome, SandboxStagingScope.BrokerSecrets)
                => Pick(src, dst, ClaudeBrokerSecrets),
            (SandboxCredentialKind.CodexHome, SandboxStagingScope.BrokerSecrets)
                => Pick(src, dst, CodexBrokerSecrets),

            // Agent home: the CLI needs its config, so keep the tree but drop the caches and history.
            (SandboxCredentialKind.ClaudeHome, _) => Trim(src, dst, ClaudeHomeExcludes),
            (SandboxCredentialKind.CodexHome, _) => Trim(src, dst, CodexHomeExcludes),

            // Small by nature, or not understood: copy whole.
            _ => Trim(src, dst),
        };

    private static string Trim(string src, string dst, params string[] excludes)
        => $"cptrim {src} {dst}{Args(excludes)}";

    private static string Pick(string src, string dst, params string[] names)
        => $"cpick {src} {dst}{Args(names)}";

    private static string Args(string[] values)
        => values.Length == 0 ? string.Empty : " " + string.Join(' ', values);
}
