using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Mintokei.Sandbox.Docker;

/// <summary>
/// <see cref="ISandboxRuntime"/> over the local Docker CLI (shelling out, matching how the rest of
/// Mintokei runs external processes). The Kubernetes backend implements the same interface later.
/// </summary>
public sealed class DockerSandboxRuntime(ILogger<DockerSandboxRuntime> logger) : ISandboxRuntime
{
    public string Backend => "docker";

    public async Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunDockerAsync(DockerCommand.BuildRunArgs(spec), ct);
        if (exit != 0)
            throw new SandboxRuntimeException($"docker run failed (exit {exit}) for '{spec.Name}': {stderr.Trim()}");

        var id = stdout.Trim();
        if (id.Length == 0)
            throw new SandboxRuntimeException($"docker run returned no container id for '{spec.Name}'");

        logger.LogInformation("Provisioned sandbox {Name} ({Id}) runtime={Runtime}",
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

        logger.LogInformation("Stopped sandbox {Name} ({Id})", handle.Name, Short(handle.Id));
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
