using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Docker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// Exercises <see cref="DockerSandboxRuntime"/> against a REAL Docker daemon (provision → status → list →
/// stop). Opt-in only — skipped unless <c>MINTOKEI_SANDBOX_DOCKER_ITEST=1</c> and the docker CLI works — so
/// normal CI never runs it. This is what proves the actual process invocation / id + status parsing, not
/// just that the arg-builder produces the right string.
/// </summary>
public class DockerSandboxRuntimeIntegrationTests
{
    [Fact]
    public async Task Provision_status_list_stop_against_real_docker()
    {
        if (!DockerAvailableAndOptedIn(out var reason))
            Assert.Skip(reason);

        var runtime = new DockerSandboxRuntime(NullLogger<DockerSandboxRuntime>.Instance);
        var spec = new SandboxSpec
        {
            Image = "alpine:latest",
            Name = $"mk-itest-{Guid.NewGuid():N}"[..24],
            RuntimeClass = "runc",
            Limits = new SandboxResourceLimits(256L * 1024 * 1024, 1, 128),
            Tmpfs = [],
            Args = ["sleep", "30"],
        };

        SandboxHandle? handle = null;
        try
        {
            handle = await runtime.ProvisionAsync(spec);
            Assert.Equal(spec.Name, handle.Name);
            Assert.Equal(SandboxState.Running, (await runtime.GetStatusAsync(handle)).State);
            Assert.Contains(await runtime.ListManagedAsync(), h => h.Name == spec.Name); // labelled + listed

            await runtime.StopAsync(handle);
            Assert.Equal(SandboxState.NotFound, (await runtime.GetStatusAsync(handle)).State);
            handle = null;
        }
        finally
        {
            if (handle is not null)
                await runtime.StopAsync(handle); // best-effort cleanup if an assert failed
        }
    }

    private static bool DockerAvailableAndOptedIn(out string reason)
    {
        if (Environment.GetEnvironmentVariable("MINTOKEI_SANDBOX_DOCKER_ITEST") != "1")
        {
            reason = "opt-in only: set MINTOKEI_SANDBOX_DOCKER_ITEST=1 to run the real-Docker test";
            return false;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            if (p.WaitForExit(10_000) && p.ExitCode == 0)
            {
                reason = "";
                return true;
            }
        }
        catch
        {
            // docker CLI not on PATH
        }

        reason = "docker CLI/daemon not available";
        return false;
    }
}
