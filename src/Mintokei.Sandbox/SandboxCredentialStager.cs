using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts;

namespace Mintokei.Sandbox;

/// <summary>Runner-host credential source paths to stage — whatever the embedder resolved (runner-home,
/// a per-workspace credential, a secret store). Any field may be null; absent sources are skipped.</summary>
public sealed record SandboxSeedSources(
    string? ClaudeConfigDir, string? ClaudeConfigJsonFile, string? CodexConfigDir, string? GitCredentialsDir);

/// <summary>Staged, sandbox-uid-readable copies. A field is null when its source was absent (drop that mount).</summary>
public sealed record StagedSeedCreds(
    string? ClaudeConfigDir, string? ClaudeConfigJsonFile, string? CodexConfigDir, string? GitCredentialsDir);

/// <summary>
/// Stages a per-session, sandbox-uid-readable COPY of the agent-CLI / git credentials on the runner, so a
/// <b>non-root</b> sandbox container (running as <see cref="SandboxImage.AgentUid"/>) can read them — the
/// runner's own <c>~/.claude</c> / git creds are root-owned, and a direct bind-mount would be unreadable, so
/// the entrypoint's copy would silently no-op and the agent would start unauthenticated. The copy is owned by
/// the sandbox uid (falling back to world-readable if the runner isn't root), lives under
/// <c>SeedStagingRoot/&lt;session&gt;</c>, and is removed with the container (<see cref="RemoveAsync"/>).
///
/// The credential SOURCE is the embedder's choice (runner-home today; a per-workspace / per-developer
/// credential later), so per-session credentials are a resolver change away — the staging itself is
/// per-session and source-agnostic. Runs entirely over <see cref="IRemoteCommandRunner"/> (the runner dials
/// out; no inbound port).
/// </summary>
public sealed class SandboxCredentialStager(IRemoteCommandRunner commandRunner, IOptions<SandboxOptions> options)
{
    private const string DefaultRoot = "/tmp/mintokei-sandbox-seed";

    private readonly string _root =
        string.IsNullOrWhiteSpace(options.Value.SeedStagingRoot) ? DefaultRoot : options.Value.SeedStagingRoot.TrimEnd('/');

    /// <summary>Stage the present sources into a per-session dir readable by the sandbox uid; returns the staged
    /// paths to bind-mount (null for a source that did not exist, so the caller drops that mount).</summary>
    public async Task<StagedSeedCreds> StageAsync(
        Guid hostMachineId, string sessionName, SandboxSeedSources sources, CancellationToken ct = default,
        long uid = SandboxImage.AgentUid)
    {
        var dir = SeedStagingDir(sessionName);
        // Paths go as POSITIONAL ARGS to `sh -c` (never interpolated into the script), so a path can't break
        // out of the script. $1=dir, $2..$5 = the four sources (empty when absent → skipped), $6 = the uid the
        // staged copy is chown'd to (the container that will MOUNT it — the sandbox by default, the broker uid
        // in nested broker mode so the non-root broker can read the creds it injects).
        var result = await commandRunner.RunAsync(hostMachineId, "/", "sh",
            ["-c", StagingScript, "mintokei-stage-seed", dir,
             sources.ClaudeConfigDir ?? "", sources.ClaudeConfigJsonFile ?? "",
             sources.CodexConfigDir ?? "", sources.GitCredentialsDir ?? "",
             uid.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            120_000, ct); // trimmed copy is fast, but allow headroom for a large cred home / slow disk

        if (result.ExitCode != 0)
            throw new SandboxRuntimeException(
                $"could not stage sandbox credentials on runner {hostMachineId} (exit {result.ExitCode}): {result.Stderr.Trim()}");

        var staged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.StartsWith("STAGED ", StringComparison.Ordinal))
                staged.Add(line["STAGED ".Length..]);

        return new StagedSeedCreds(
            staged.Contains(".claude") ? $"{dir}/.claude" : null,
            staged.Contains(".claude.json") ? $"{dir}/.claude.json" : null,
            staged.Contains(".codex") ? $"{dir}/.codex" : null,
            staged.Contains("git") ? $"{dir}/git" : null);
    }

    /// <summary>Remove a session's staged credential copy. Best-effort; never throws (called from cleanup paths).</summary>
    public async Task RemoveAsync(Guid hostMachineId, string sessionName, CancellationToken ct = default)
    {
        try
        {
            await commandRunner.RunAsync(hostMachineId, "/", "rm", ["-rf", SeedStagingDir(sessionName)], 15_000, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: the session's container is already gone. What catches a miss is SweepAsync — NOT a
            // reboot, which is what this comment used to claim and which never comes on a long-lived runner.
            _ = ex;
        }
    }

    /// <summary>
    /// Remove staged credential copies that no live session owns, and report how many went.
    ///
    /// <para><see cref="RemoveAsync"/> is best-effort by design — it runs on cleanup paths that must not throw —
    /// so a copy outlives its session whenever teardown is interrupted (host restart between provision and
    /// recycle, a crashed reconcile, a killed process). Nothing collected those: the assumption was that a reboot
    /// would, and on a runner that stays up for weeks it does not. A staged copy is a real credential, so it must
    /// be swept against a live inventory rather than left to time.</para>
    ///
    /// <para>Worse than merely lingering: a leaked copy does not decay into a harmless stale token. Deployments
    /// that keep the broker's staged token in sync with the rotating host token (so a mid-session rotation does
    /// not 401) refresh <em>every</em> staged copy they find, so an orphan is kept permanently VALID at a
    /// predictable path. That is the failure this exists to end.</para>
    ///
    /// <para><paramref name="minimumAgeMinutes"/> is what makes this safe to run against a live host: credentials
    /// are staged BEFORE the container is created, so a sandbox mid-provision legitimately has a staged copy and
    /// no container yet. Only copies older than that window are candidates, which puts them well past any
    /// provision. Never throws.</para>
    /// </summary>
    /// <param name="hostMachineId">The machine holding the staging root.</param>
    /// <param name="liveSessionNames">Names of sessions that still exist — anything else is an orphan. Pass the
    /// backend's own container inventory, not the caller's records: a caller that lost track of a sandbox is
    /// exactly the case this cleans up after.</param>
    /// <param name="minimumAgeMinutes">Grace window; copies younger than this are never removed.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<int> SweepAsync(
        Guid hostMachineId,
        IReadOnlyCollection<string> liveSessionNames,
        int minimumAgeMinutes = DefaultSweepGraceMinutes,
        CancellationToken ct = default)
    {
        try
        {
            // Live names go as positional args, sanitized the same way the staging dir was, so the comparison is
            // against the dir name that Stage actually created rather than the raw session name.
            string[] argv =
            [
                "-c", SweepScript, "mintokei-sweep-seed", _root,
                Math.Max(0, minimumAgeMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture),
                .. liveSessionNames.Select(SanitizeSegment),
            ];

            var result = await commandRunner.RunAsync(hostMachineId, "/", "sh", argv, 30_000, ct);
            if (result.ExitCode != 0)
                return 0;

            return (result.Stdout ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Count(line => line.StartsWith("SWEPT ", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex; // best-effort: a worker that is unreachable now is swept on the next reconcile
            return 0;
        }
    }

    /// <summary>Default grace window before a staged copy with no session is considered an orphan. Comfortably
    /// longer than any provision, since staging happens before the container exists.</summary>
    public const int DefaultSweepGraceMinutes = 10;

    private string SeedStagingDir(string sessionName) => $"{_root}/{SanitizeSegment(sessionName)}";

    // A single path segment safe to interpolate into a staging path — no '.', so no '..' traversal, and no
    // separators. Session/machine names are already tame (e.g. "sbx-<hex>"); this is defence in depth.
    private static string SanitizeSegment(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "session" : new string(chars);
    }

    // POSIX sh. Stages each present source into the staging dir, echoes a STAGED marker per success, then hands
    // ownership to the uid passed as $6 (the container that mounts the copy — sandbox by default, broker uid in
    // nested broker mode) — or, if the runner isn't root and chown fails, makes the copies world-readable.
    //
    // WHAT gets copied is not decided here: SandboxCredentialStaging owns that for every staging site (this one
    // and the two Kubernetes init containers), so a trim learned on one path can't go missing on another. This
    // stages an AGENT HOME — the sandbox runs the CLI, so it needs the config, just not the runner's history.
    private static readonly string StagingScript = $$"""
        set -eu
        S=$1
        rm -rf "$S"
        mkdir -p "$S"
        {{SandboxCredentialStaging.ShellFunctions}}
        if [ -n "$2" ] && [ -e "$2" ]; then {{ClaudeHomeCopy}}; echo "STAGED .claude"; fi
        if [ -n "$3" ] && [ -e "$3" ]; then {{ClaudeJsonCopy}}; echo "STAGED .claude.json"; fi
        if [ -n "$4" ] && [ -e "$4" ]; then {{CodexHomeCopy}}; echo "STAGED .codex"; fi
        if [ -n "$5" ]; then
          g=0
          if [ -e "$5/.git-credentials" ]; then mkdir -p "$S/git"; cp -aL "$5/.git-credentials" "$S/git/" 2>/dev/null || true; g=1; fi
          if [ -e "$5/.ssh" ]; then mkdir -p "$S/git"; cp -aL "$5/.ssh" "$S/git/" 2>/dev/null || true; g=1; fi
          if [ "$g" = 1 ]; then echo "STAGED git"; fi
        fi
        chown -R "$6":"$6" "$S" 2>/dev/null || chmod -R a+rX "$S"
        """;

    // POSIX sh. $1 = staging root, $2 = minimum age in minutes, $3.. = the sanitized names of live sessions.
    // Removes every immediate child dir of the root that is (a) not a live session and (b) older than the grace
    // window, echoing a SWEPT marker per removal.
    //
    // The age test reads the SESSION dir's own mtime, which only changes when the copy is staged — a token
    // re-sync writes .claude/.credentials.json, which touches that file and its parent, not this dir. So a
    // synced-but-orphaned copy still ages out, which is precisely the case worth removing.
    //
    // Written with explicit `if` rather than `&&` chains: under `set -e` a trailing false test in an AND-list
    // is a footgun, and this script deletes things.
    private const string SweepScript = """
        set -eu
        R=$1
        A=$2
        shift 2
        [ -d "$R" ] || exit 0
        for d in "$R"/*/; do
          [ -d "$d" ] || continue
          n=$(basename "$d")
          keep=0
          for l in "$@"; do
            if [ "$n" = "$l" ]; then keep=1; break; fi
          done
          if [ "$keep" = 1 ]; then continue; fi
          if [ -n "$(find "$R" -mindepth 1 -maxdepth 1 -type d -name "$n" -mmin +"$A" 2>/dev/null)" ]; then
            rm -rf "$d"
            echo "SWEPT $n"
          fi
        done
        """;

    private static string ClaudeHomeCopy => SandboxCredentialStaging.CopyCommand(
        "\"$2\"", "\"$S/.claude\"", SandboxCredentialKind.ClaudeHome, SandboxStagingScope.AgentHome);

    private static string ClaudeJsonCopy => SandboxCredentialStaging.CopyCommand(
        "\"$3\"", "\"$S/.claude.json\"", SandboxCredentialKind.ClaudeJson, SandboxStagingScope.AgentHome);

    private static string CodexHomeCopy => SandboxCredentialStaging.CopyCommand(
        "\"$4\"", "\"$S/.codex\"", SandboxCredentialKind.CodexHome, SandboxStagingScope.AgentHome);
}
