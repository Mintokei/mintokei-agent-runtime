using System.Diagnostics;
using Mintokei.Runner.Contracts;
using Mintokei.Runner.Contracts.Messages;

namespace Mintokei.Sandbox.Docker;

/// <summary>
/// An <see cref="IRemoteCommandRunner"/> that runs commands on the LOCAL host via a child process — the
/// worker-free counterpart to the gRPC runner. It lets the whole remote-sandbox path
/// (<see cref="RemoteDockerSandboxRuntime"/>, <see cref="RemoteSandboxBroker"/>,
/// <see cref="SandboxCredentialStager"/>, <see cref="RemoteSandboxManager"/>) run entirely on THIS machine with
/// no enrolled worker: the "worker" is localhost, so <paramref name="machineId"/> is ignored. Intended for
/// single-host / local-dev broker runs; production still dispatches to a real worker over gRPC.
/// </summary>
public sealed class LocalCommandRunner : IRemoteCommandRunner
{
    public async Task<RunCommandResponse> RunAsync(
        Guid machineId, string workingDirectory, string executable,
        IReadOnlyList<string> args, int timeoutMs, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a); // pre-encoded argv — no shell, so no injection

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception ex)
        {
            return new RunCommandResponse(Id(), 127, "", "", $"{executable}: {ex.Message}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new RunCommandResponse(Id(), 124, await stdout, await stderr, "timeout");
        }

        return new RunCommandResponse(Id(), process.ExitCode, await stdout, await stderr, null);
    }

    private static string Id() => Guid.NewGuid().ToString("N");
}
