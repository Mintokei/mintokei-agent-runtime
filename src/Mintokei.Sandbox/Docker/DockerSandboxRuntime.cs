using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mintokei.Sandbox.Docker;

/// <summary>
/// <see cref="ISandboxRuntime"/> over the local Docker CLI (shelling out, matching how the rest of
/// Mintokei runs external processes). The Kubernetes backend implements the same interface later.
/// </summary>
public sealed class DockerSandboxRuntime
    : ISandboxRuntime, ISandboxLogSource, ISandboxAdmissionSource, ISandboxWorkspaceStore
{
    private readonly ILogger<DockerSandboxRuntime> _logger;
    private readonly SandboxCredentialStager _seedStager;

    public DockerSandboxRuntime(ILogger<DockerSandboxRuntime> logger, IOptions<SandboxOptions> options)
    {
        _logger = logger;
        // Built over the LOCAL process runner rather than resolved from DI: staging has to happen on the machine
        // that will run the container, and this backend runs it here. On a host that also has enrolled workers
        // the ambient IRemoteCommandRunner dispatches over gRPC, which would stage the copy on the wrong machine.
        _seedStager = new SandboxCredentialStager(new LocalCommandRunner(), options);
    }

    /// <summary>Test seam: inject the stager. Production always stages locally — see the public constructor.</summary>
    internal DockerSandboxRuntime(ILogger<DockerSandboxRuntime> logger, SandboxCredentialStager seedStager)
    {
        _logger = logger;
        _seedStager = seedStager;
    }

    public string Backend => "docker";

    public async Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        spec = await StageSeedCredentialsAsync(spec, ct);
        spec = await EnsurePersistentWorkspaceAsync(spec, ct);

        var (exit, stdout, stderr) = await RunDockerAsync(DockerCommand.BuildRunArgs(spec), ct);
        if (exit != 0)
            throw new SandboxRuntimeException($"docker run failed (exit {exit}) for '{spec.Name}': {stderr.Trim()}");

        var id = stdout.Trim();
        if (id.Length == 0)
            throw new SandboxRuntimeException($"docker run returned no container id for '{spec.Name}'");

        _logger.LogInformation("Provisioned sandbox {Name} ({Id}) runtime={Runtime}",
            spec.Name, Short(id), spec.RuntimeClass);
        return new SandboxHandle(id, spec.Name, Backend);
    }

    public async Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunDockerAsync(
            ["inspect", "--format", "{{.State.Status}} {{.State.ExitCode}}", handle.Id], ct);

        if (exit != 0)
        {
            return stderr.Contains("No such object", StringComparison.OrdinalIgnoreCase)
                ? new SandboxStatus(SandboxState.NotFound)
                : new SandboxStatus(SandboxState.Unknown, Detail: stderr.Trim());
        }

        var parts = stdout.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var state = parts.Length > 0 ? MapState(parts[0]) : SandboxState.Unknown;
        int? code = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : null;
        return new SandboxStatus(state, code);
    }

    public async Task StopAsync(SandboxHandle handle, CancellationToken ct = default)
    {
        var (exit, _, stderr) = await RunDockerAsync(["rm", "--force", handle.Id], ct);
        if (exit != 0 && !stderr.Contains("No such", StringComparison.OrdinalIgnoreCase))
            throw new SandboxRuntimeException($"docker rm failed for '{handle.Name}': {stderr.Trim()}");

        // The staged credential copy outlives nothing: it exists only for this container. Best-effort by
        // contract, so a cleanup failure never masks a successful stop.
        await _seedStager.RemoveAsync(Guid.Empty, handle.Name, ct);

        _logger.LogInformation("Stopped sandbox {Name} ({Id})", handle.Name, Short(handle.Id));
    }

    /// <summary>
    /// Back <c>/repos</c> with a named volume keyed by <see cref="SandboxSpec.PersistentWorkspaceKey"/>, so the
    /// whole working tree — every repo in the session, plus the agent-CLI transcript the entrypoint symlinks onto
    /// it — survives this container being recycled.
    ///
    /// Docker volumes must exist before <c>docker run</c>, which is why this is a step here rather than something
    /// <see cref="DockerCommand"/> can express: without it the key is accepted and silently does nothing, and a
    /// recycled session comes back with an empty tree and no transcript to <c>--resume</c> from — a failure that
    /// surfaces far from its cause. Kubernetes honours the same key with a PVC it creates itself; the nested path
    /// does exactly this.
    ///
    /// Only when the session actually has repos: with no working tree there is nothing worth keeping.
    /// </summary>
    private async Task<SandboxSpec> EnsurePersistentWorkspaceAsync(SandboxSpec spec, CancellationToken ct)
    {
        if (spec.PersistentWorkspaceKey is not { } key || !spec.Env.ContainsKey(SandboxSpecFactory.ReposEnvVar))
            return spec;

        var volumeName = SandboxWorkspaceStore.Name(key);
        var (exit, _, stderr) = await RunDockerAsync(
            ["volume", "create",
             "--label", $"{DockerCommand.ManagedLabel}=1",
             "--label", $"{SandboxWorkspaceStore.LabelKey}={key:N}",
             volumeName], ct);
        if (exit != 0)
            throw new SandboxRuntimeException(
                $"could not create workspace volume '{volumeName}': {stderr.Trim()}");

        _logger.LogInformation("Persisting workspace for key {Key} on volume {Volume} (mounted at {Path})",
            key, volumeName, SandboxSpecFactory.RepoRoot);

        // DockerCommand drops the tmpfs for any path that is also a mount, so the volume wins at /repos.
        return spec with
        {
            Mounts = [.. spec.Mounts, new SandboxMount(volumeName, SandboxSpecFactory.RepoRoot, ReadOnly: false)],
        };
    }

    // --- ISandboxWorkspaceStore: reaper-driven GC of the per-workspace volumes created above ---

    public async Task<IReadOnlyList<Guid>> ListPersistentWorkspaceKeysAsync(CancellationToken ct = default)
    {
        var (exit, stdout, _) = await RunDockerAsync(
            ["volume", "ls", "--filter", $"label={DockerCommand.ManagedLabel}=1", "--format", "{{.Name}}"], ct);
        if (exit != 0)
            return [];

        var keys = new List<Guid>();
        foreach (var name in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (SandboxWorkspaceStore.TryParseKey(name, out var key))
                keys.Add(key);
        return keys;
    }

    /// <summary>
    /// Best-effort removal of a persisted workspace. Docker refuses while a container still mounts the volume —
    /// the safety net against deleting a live session's tree — and that refusal must NOT read as success: a
    /// caller mirroring the deletion into its own state (dropping a session id that lives on this volume) would
    /// otherwise act on a tree that is still there. True means genuinely gone; retried on a later sweep.
    /// </summary>
    public async Task<bool> RemovePersistentWorkspaceAsync(Guid key, CancellationToken ct = default)
    {
        try
        {
            var volumeName = SandboxWorkspaceStore.Name(key);
            var (exit, _, stderr) = await RunDockerAsync(["volume", "rm", volumeName], ct);
            if (exit == 0)
                return true;

            // Already absent counts as removed — the post-condition the caller cares about is "not there".
            if (stderr.Contains("No such volume", StringComparison.OrdinalIgnoreCase))
                return true;

            _logger.LogDebug("docker volume rm '{Name}' did not remove it: {Err}", volumeName, stderr.Trim());
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "could not remove workspace volume for {Key}", key);
            return false;
        }
    }

    /// <summary>Container path prefix under which the spec factory mounts agent-CLI / git credentials.</summary>
    private const string SeedRoot = "/seed";

    /// <summary>
    /// Replace raw credential bind-mounts with a sandbox-uid-readable COPY.
    ///
    /// The sandbox container runs as <see cref="SandboxImage.AgentUid"/>, but a host's agent-CLI credentials are
    /// root-owned <c>0600</c>/<c>0700</c> (that is how the CLIs write them). Bind-mounting those directly leaves
    /// them unreadable inside the container — and the entrypoint copies <c>/seed</c> into HOME under
    /// <c>set -e</c>, so the failed <c>cp</c> kills the container before the runner ever starts. The failure
    /// then surfaces as a bare "exited (exit code 1) before its agent runner could connect".
    ///
    /// The other two backends already avoid this — Kubernetes stages via a root initContainer, the nested/remote
    /// path via <see cref="SandboxCredentialStager"/> on the worker. This is the same step for local Docker, so
    /// all three agree. Broker-egress sessions never reach here: they seed no credentials at all by design.
    /// </summary>
    internal async Task<SandboxSpec> StageSeedCredentialsAsync(SandboxSpec spec, CancellationToken ct)
    {
        static bool IsSeed(SandboxMount m) =>
            m.Target == SeedRoot || m.Target.StartsWith(SeedRoot + "/", StringComparison.Ordinal);

        if (!spec.Mounts.Any(IsSeed))
            return spec;

        string? SourceOf(string target) =>
            spec.Mounts.FirstOrDefault(m => m.Target == target)?.Source;

        var staged = await _seedStager.StageAsync(Guid.Empty, spec.Name, new SandboxSeedSources(
            ClaudeConfigDir: SourceOf($"{SeedRoot}/.claude"),
            ClaudeConfigJsonFile: SourceOf($"{SeedRoot}/.claude.json"),
            CodexConfigDir: SourceOf($"{SeedRoot}/.codex"),
            GitCredentialsDir: SourceOf($"{SeedRoot}/git")), ct);

        // A source that did not exist stages as null — drop that mount rather than binding a missing path.
        var mounts = spec.Mounts.Where(m => !IsSeed(m)).ToList();
        AddStaged(mounts, staged.ClaudeConfigDir, $"{SeedRoot}/.claude");
        AddStaged(mounts, staged.ClaudeConfigJsonFile, $"{SeedRoot}/.claude.json");
        AddStaged(mounts, staged.CodexConfigDir, $"{SeedRoot}/.codex");
        AddStaged(mounts, staged.GitCredentialsDir, $"{SeedRoot}/git");

        _logger.LogDebug("Staged {Count} credential mount(s) for sandbox {Name} readable by uid {Uid}",
            mounts.Count(IsSeed), spec.Name, SandboxImage.AgentUid);
        return spec with { Mounts = mounts };

        static void AddStaged(List<SandboxMount> mounts, string? source, string target)
        {
            if (!string.IsNullOrWhiteSpace(source))
                mounts.Add(new SandboxMount(source, target, ReadOnly: true));
        }
    }

    /// <summary>
    /// Read back what this sandbox was provisioned to serve, from the label <see cref="DockerCommand"/> stamped
    /// on it at launch. Read off the CONTAINER rather than from any caller's record: a record can be stale after
    /// a restart or a rollback, and the sandbox itself is the only authority on what its broker actually serves.
    ///
    /// A container with no such label is undeclared, not "admits nothing" — it predates the declaration or was
    /// launched single-session. <see cref="SandboxAdmission"/> decides what that means; reporting it faithfully
    /// as empty is this method's whole job.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAdmittedToolsAsync(
        SandboxHandle handle, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunDockerAsync(
            ["inspect", "--format", $"{{{{index .Config.Labels \"{DockerCommand.AdmittedToolsLabel}\"}}}}", handle.Id], ct);

        if (exit != 0)
        {
            if (stderr.Contains("No such object", StringComparison.OrdinalIgnoreCase))
                return [];
            throw new SandboxRuntimeException(
                $"could not read the admitted tools of sandbox '{handle.Name}': {stderr.Trim()}");
        }

        // Docker prints "<no value>" for a label that isn't set — an absent declaration, same as blank.
        var value = stdout.Trim();
        return value is "<no value>" ? [] : SandboxAdmission.ParseAdmittedTools(value);
    }

    public async Task<string> GetLogsAsync(SandboxHandle handle, int tailLines = 40, CancellationToken ct = default)
    {
        try
        {
            // `docker logs` writes the container's stdout to our stdout and its stderr to our stderr; the
            // clone failure we want lands on stderr, so combine both (stderr first — it's the error stream).
            var (exit, stdout, stderr) = await RunDockerAsync(
                ["logs", "--tail", tailLines.ToString(CultureInfo.InvariantCulture), handle.Id], ct);
            if (exit != 0)
                return string.Empty;
            return string.Join('\n',
                new[] { stderr.Trim(), stdout.Trim() }.Where(s => s.Length > 0));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "docker logs failed for {Name}", handle.Name);
            return string.Empty;
        }
    }

    public async Task<IReadOnlyList<SandboxHandle>> ListManagedAsync(CancellationToken ct = default)
    {
        // `-a` includes exited containers; the label filter keeps it to sandboxes we launched.
        var (exit, stdout, stderr) = await RunDockerAsync(
            ["ps", "--all", "--filter", $"label={DockerCommand.ManagedLabel}", "--format", "{{.ID}}\t{{.Names}}"], ct);
        if (exit != 0)
            throw new SandboxRuntimeException($"docker ps failed: {stderr.Trim()}");

        var handles = new List<SandboxHandle>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length == 2 && parts[0].Length > 0)
                handles.Add(new SandboxHandle(parts[0], parts[1], Backend));
        }

        return handles;
    }

    private static SandboxState MapState(string status) => status.ToLowerInvariant() switch
    {
        "created" => SandboxState.Pending,
        "running" => SandboxState.Running,
        "exited" or "dead" => SandboxState.Exited,
        _ => SandboxState.Unknown,
    };

    private static string Short(string id) => id[..Math.Min(12, id.Length)];

    private static async Task<(int Exit, string Stdout, string Stderr)> RunDockerAsync(
        IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new SandboxRuntimeException("failed to launch the docker CLI (is Docker installed and on PATH?)", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout, await stderr);
    }
}
