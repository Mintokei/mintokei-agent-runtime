using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts;

namespace Mintokei.Sandbox.Docker;

/// <summary>
/// Runs a sandbox container ON A CHOSEN REMOTE WORKER by dispatching the SAME <c>docker</c> commands the
/// local <see cref="DockerSandboxRuntime"/> builds — but over the runner control channel
/// (<see cref="IRemoteCommandRunner"/>) instead of a local docker socket. This is the "nested Docker on a
/// worker" path: the worker dials out, so the sandbox needs no inbound port, and the exact same
/// <see cref="DockerCommand.BuildRunArgs"/> shape is reused so the argv can never drift from the local backend.
///
/// Machine-targeted (every method takes the worker's machine id), so it is deliberately NOT an
/// <see cref="ISandboxRuntime"/> — that seam is host-agnostic. Credential staging for the non-root container
/// is a separate concern (<see cref="SandboxCredentialStager"/>); this type only provisions and manages
/// containers + per-session persisted volumes.
/// </summary>
public sealed class RemoteDockerSandboxRuntime(
    IRemoteCommandRunner commandRunner,
    IOptions<SandboxOptions> options,
    ILogger<RemoteDockerSandboxRuntime> logger)
{
    /// <summary>Backend tag on handles this runtime produces (mirrors <see cref="DockerSandboxRuntime"/>'s "docker").</summary>
    public const string Backend = "docker-remote";

    /// <summary>Named-volume prefix for a persisted per-session working tree.</summary>
    public const string WorkspaceVolumePrefix = "mintokei-ws-";

    /// <summary>Docker label recording which task a persisted workspace volume belongs to (for GC).</summary>
    public const string TaskLabel = "mintokei.task";

    private readonly int _runTimeoutMs = Math.Max(10_000, options.Value.RemoteRunTimeoutSeconds * 1000);

    /// <summary>Deterministic per-task volume name — stable across recycle/resume so a continued session remounts its tree.</summary>
    public static string WorkspaceVolumeName(Guid taskId) => WorkspaceVolumePrefix + taskId.ToString("N");

    /// <summary>Parse the task id back out of a <see cref="WorkspaceVolumeName"/>; false for any other volume.</summary>
    public static bool TryParseWorkspaceTaskId(string volumeName, out Guid taskId)
    {
        taskId = default;
        return volumeName.StartsWith(WorkspaceVolumePrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(volumeName[WorkspaceVolumePrefix.Length..], "N", out taskId);
    }

    public async Task<SandboxHandle> ProvisionAsync(Guid hostMachineId, SandboxSpec spec, CancellationToken ct = default)
    {
        int exit; string stdout, stderr;
        try
        {
            (exit, stdout, stderr) = await DockerAsync(hostMachineId, DockerCommand.BuildRunArgs(spec), _runTimeoutMs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SandboxRuntimeException(
                $"could not dispatch 'docker run' to runner {hostMachineId} (is it online with Docker on PATH?): {ex.Message}", ex);
        }

        if (exit != 0)
            throw new SandboxRuntimeException(
                $"docker run failed on runner {hostMachineId} (exit {exit}) for '{spec.Name}': {stderr.Trim()}");

        var id = stdout.Trim();
        if (id.Length == 0)
            throw new SandboxRuntimeException($"docker run returned no container id on runner {hostMachineId} for '{spec.Name}'");

        logger.LogInformation("Provisioned nested sandbox {Name} ({Id}) on runner {Host}", spec.Name, Short(id), hostMachineId);
        return new SandboxHandle(id, spec.Name, Backend);
    }

    public async Task<SandboxStatus> GetStatusAsync(Guid hostMachineId, SandboxHandle handle, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await DockerAsync(
            hostMachineId, ["inspect", "--format", "{{.State.Status}} {{.State.ExitCode}}", handle.Id], 15_000, ct);

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

    public async Task StopAsync(Guid hostMachineId, SandboxHandle handle, CancellationToken ct = default)
    {
        try
        {
            var (exit, _, stderr) = await DockerAsync(hostMachineId, ["rm", "--force", handle.Id], 15_000, ct);
            if (exit != 0 && !stderr.Contains("No such", StringComparison.OrdinalIgnoreCase))
                logger.LogWarning("docker rm failed on runner {Host} for '{Name}': {Err}", hostMachineId, handle.Name, stderr.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort cleanup — a GC pass covers a lingering container.
            logger.LogWarning(ex, "could not dispatch 'docker rm' to runner {Host} for '{Name}'", hostMachineId, handle.Name);
        }
    }

    /// <summary>Best-effort tail of the container's combined stdout/stderr; empty on any failure (never throws).</summary>
    public async Task<string> GetLogsAsync(Guid hostMachineId, SandboxHandle handle, int tailLines = 40, CancellationToken ct = default)
    {
        try
        {
            var (exit, stdout, stderr) = await DockerAsync(
                hostMachineId, ["logs", "--tail", tailLines.ToString(CultureInfo.InvariantCulture), handle.Id], 15_000, ct);
            if (exit != 0)
                return string.Empty;
            return string.Join('\n', new[] { stderr.Trim(), stdout.Trim() }.Where(s => s.Length > 0));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "docker logs failed on runner {Host} for {Name}", hostMachineId, handle.Name);
            return string.Empty;
        }
    }

    /// <summary>Capability probe: does the target worker have a working Docker on PATH? Never throws.</summary>
    public async Task<bool> ProbeDockerAsync(Guid hostMachineId, CancellationToken ct = default)
    {
        try
        {
            var (exit, stdout, _) = await DockerAsync(hostMachineId, ["version", "--format", "{{.Server.Version}}"], 10_000, ct);
            return exit == 0 && stdout.Trim().Length > 0;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "docker capability probe failed on runner {Host}", hostMachineId);
            return false;
        }
    }

    /// <summary>Probe the worker's $HOME so cred mounts point at THAT worker's own ~/.claude etc. Defaults to /root.</summary>
    public async Task<string> ProbeHomeAsync(Guid hostMachineId, CancellationToken ct = default)
    {
        try
        {
            var r = await commandRunner.RunAsync(hostMachineId, "/", "printenv", ["HOME"], 5000, ct);
            var home = r.Stdout?.Trim();
            return r.ExitCode == 0 && !string.IsNullOrWhiteSpace(home) ? home : "/root";
        }
        catch
        {
            return "/root"; // conservative default; cred mounts just won't resolve and the agent will report it
        }
    }

    /// <summary>Idempotently create the persisted per-session workspace volume, labelled so a GC pass can find it.</summary>
    public async Task EnsureWorkspaceVolumeAsync(Guid hostMachineId, string volumeName, Guid taskId, CancellationToken ct = default)
    {
        var (exit, _, stderr) = await DockerAsync(hostMachineId,
            ["volume", "create", "--label", $"{DockerCommand.ManagedLabel}=1", "--label", $"{TaskLabel}={taskId:N}", volumeName],
            15_000, ct);
        if (exit != 0)
            throw new SandboxRuntimeException(
                $"could not create workspace volume '{volumeName}' on runner {hostMachineId}: {stderr.Trim()}");
    }

    /// <summary>Names of every persisted workspace volume on the worker (label-filtered to ours). Empty on any error.</summary>
    public async Task<IReadOnlyList<string>> ListWorkspaceVolumesAsync(Guid hostMachineId, CancellationToken ct = default)
    {
        var (exit, stdout, _) = await DockerAsync(hostMachineId,
            ["volume", "ls", "--filter", $"label={DockerCommand.ManagedLabel}=1", "--format", "{{.Name}}"], 15_000, ct);
        if (exit != 0)
            return [];
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Best-effort removal of a persisted workspace volume. Docker refuses while a container still mounts it
    /// (the safety net against removing a live session's tree); retried on a later tick. Never throws.</summary>
    public async Task RemoveVolumeAsync(Guid hostMachineId, string volumeName, CancellationToken ct = default)
    {
        try
        {
            var (exit, _, stderr) = await DockerAsync(hostMachineId, ["volume", "rm", volumeName], 15_000, ct);
            if (exit != 0 && !stderr.Contains("No such volume", StringComparison.OrdinalIgnoreCase))
                logger.LogDebug("docker volume rm '{Name}' on runner {Host} did not remove it: {Err}", volumeName, hostMachineId, stderr.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "could not dispatch 'docker volume rm' to runner {Host} for '{Name}'", hostMachineId, volumeName);
        }
    }

    private async Task<(int Exit, string Stdout, string Stderr)> DockerAsync(
        Guid hostMachineId, IReadOnlyList<string> argv, int timeoutMs, CancellationToken ct)
    {
        var r = await commandRunner.RunAsync(hostMachineId, "/tmp", "docker", argv, timeoutMs, ct);
        return (r.ExitCode, r.Stdout ?? string.Empty, r.Stderr ?? string.Empty);
    }

    private static SandboxState MapState(string status) => status.ToLowerInvariant() switch
    {
        "created" => SandboxState.Pending,
        "running" => SandboxState.Running,
        "exited" or "dead" => SandboxState.Exited,
        _ => SandboxState.Unknown,
    };

    private static string Short(string id) => id[..Math.Min(12, id.Length)];
}
