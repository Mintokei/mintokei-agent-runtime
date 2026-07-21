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
        Guid hostMachineId, string sessionName, SandboxSeedSources sources, CancellationToken ct = default)
    {
        var dir = SeedStagingDir(sessionName);
        // Paths go as POSITIONAL ARGS to `sh -c` (never interpolated into the script), so a path can't break
        // out of the script. $1=dir, $2..$5 = the four sources (empty string when absent → the script skips it).
        var result = await commandRunner.RunAsync(hostMachineId, "/", "sh",
            ["-c", StagingScript, "mintokei-stage-seed", dir,
             sources.ClaudeConfigDir ?? "", sources.ClaudeConfigJsonFile ?? "",
             sources.CodexConfigDir ?? "", sources.GitCredentialsDir ?? ""],
            30_000, ct);

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
            _ = ex; // best-effort: the session's container is already gone; a stale copy is GC'd on reboot
        }
    }

    private string SeedStagingDir(string sessionName) => $"{_root}/{SanitizeSegment(sessionName)}";

    // A single path segment safe to interpolate into a staging path — no '.', so no '..' traversal, and no
    // separators. Session/machine names are already tame (e.g. "sbx-<hex>"); this is defence in depth.
    private static string SanitizeSegment(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "session" : new string(chars);
    }

    // POSIX sh, no braces (so raw-string {{ }} only marks the interpolated uid). Copies each present source into
    // the staging dir, echoes a STAGED marker per success, then hands ownership to the sandbox uid (or, if the
    // runner isn't root and chown fails, makes the copies world-readable so the uid can still read them).
    private static readonly string StagingScript = $$"""
        set -eu
        S=$1
        rm -rf "$S"
        mkdir -p "$S"
        if [ -n "$2" ] && [ -e "$2" ]; then cp -aL "$2" "$S/.claude"; echo "STAGED .claude"; fi
        if [ -n "$3" ] && [ -e "$3" ]; then cp -aL "$3" "$S/.claude.json"; echo "STAGED .claude.json"; fi
        if [ -n "$4" ] && [ -e "$4" ]; then cp -aL "$4" "$S/.codex"; echo "STAGED .codex"; fi
        if [ -n "$5" ]; then
          g=0
          if [ -e "$5/.git-credentials" ]; then mkdir -p "$S/git"; cp -aL "$5/.git-credentials" "$S/git/"; g=1; fi
          if [ -e "$5/.ssh" ]; then mkdir -p "$S/git"; cp -aL "$5/.ssh" "$S/git/"; g=1; fi
          if [ "$g" = 1 ]; then echo "STAGED git"; fi
        fi
        chown -R {{SandboxImage.AgentUid}}:{{SandboxImage.AgentUid}} "$S" 2>/dev/null || chmod -R a+rX "$S"
        """;
}
