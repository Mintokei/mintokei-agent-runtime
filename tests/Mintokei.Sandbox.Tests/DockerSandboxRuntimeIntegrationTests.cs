using System.Diagnostics;
using Microsoft.Extensions.Options;
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

        var runtime = new DockerSandboxRuntime(
            NullLogger<DockerSandboxRuntime>.Instance, Options.Create(new SandboxOptions()));
        var spec = new SandboxSpec
        {
            Image = "alpine:latest",
            Name = $"mk-itest-{Guid.NewGuid():N}"[..24],
            RuntimeClass = "runc",
            Limits = new SandboxResources(256L * 1024 * 1024, 1, 128),
            Tmpfs = [],
            Args = ["sleep", "30"],
            AdmittedTools = ["ClaudeCodeCli"],
        };

        SandboxHandle? handle = null;
        try
        {
            handle = await runtime.ProvisionAsync(spec);
            Assert.Equal(spec.Name, handle.Name);
            Assert.Equal(SandboxState.Running, (await runtime.GetStatusAsync(handle)).State);
            Assert.Contains(await runtime.ListManagedAsync(), h => h.Name == spec.Name); // labelled + listed

            // The declaration must survive the round trip through Docker: sharing depends on reading back what
            // a RUNNING sandbox serves, and a label that writes but doesn't parse fails closed (no sharing).
            Assert.Equal(["ClaudeCodeCli"], await runtime.GetAdmittedToolsAsync(handle));

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

    /// <summary>
    /// The persisted workspace must OUTLIVE the container — that is the entire feature. A session goes idle, the
    /// reaper tears the container down, and the next turn re-provisions: the working tree and the agent-CLI
    /// transcript have to still be there or `--resume` fails with "no conversation found".
    ///
    /// Written against real Docker because the failure this guards was a silently-ignored key: everything
    /// type-checked, the container ran, and only a later resume revealed the tree had never been persisted.
    /// </summary>
    [Fact]
    public async Task Persistent_workspace_volume_outlives_the_container()
    {
        if (!DockerAvailableAndOptedIn(out var reason))
            Assert.Skip(reason);

        var runtime = new DockerSandboxRuntime(
            NullLogger<DockerSandboxRuntime>.Instance, Options.Create(new SandboxOptions()));
        var key = Guid.NewGuid();
        var spec = new SandboxSpec
        {
            Image = "alpine:latest",
            Name = $"mk-ws-{Guid.NewGuid():N}"[..24],
            RuntimeClass = "runc",
            Limits = new SandboxResources(256L * 1024 * 1024, 1, 128),
            Tmpfs = [],
            Args = ["sleep", "30"],
            PersistentWorkspaceKey = key,
            // The store exists only for a session that HAS a working tree, so the repo list is what switches it on.
            Env = new Dictionary<string, string>
            {
                [SandboxSpecFactory.ReposEnvVar] = "https://example.invalid/r.git|/repos/r|",
            },
        };

        SandboxHandle? handle = null;
        try
        {
            handle = await runtime.ProvisionAsync(spec);
            Assert.Contains(key, await runtime.ListPersistentWorkspaceKeysAsync());

            // Docker refuses to remove a volume a live container still mounts. That refusal must report FALSE,
            // not success — a caller mirroring the deletion would otherwise drop state for a tree still in use.
            Assert.False(await runtime.RemovePersistentWorkspaceAsync(key));

            await runtime.StopAsync(handle);
            handle = null;
            Assert.Contains(key, await runtime.ListPersistentWorkspaceKeysAsync()); // survived the container

            Assert.True(await runtime.RemovePersistentWorkspaceAsync(key));
            Assert.DoesNotContain(key, await runtime.ListPersistentWorkspaceKeysAsync());
        }
        finally
        {
            if (handle is not null)
                await runtime.StopAsync(handle);
            await runtime.RemovePersistentWorkspaceAsync(key); // best-effort if an assert failed
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
