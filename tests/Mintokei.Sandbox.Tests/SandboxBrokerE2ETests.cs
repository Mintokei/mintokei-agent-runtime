using System.Diagnostics;
using Mintokei.Sandbox.Docker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// The full broker e2e against a REAL Docker daemon: builds the broker image (Dockerfile.broker), stands up the
/// per-session <c>--internal</c> network with the broker dual-homed on it, and drives a real <c>git clone</c>
/// from a sandbox container joined to that network. Asserts the three properties that make broker egress real:
/// an allowlisted host clones THROUGH the broker, a non-allowlisted host is blocked, and with no proxy there is
/// no egress at all (deny-by-default). Opt-in only (needs Docker + internet) — skipped unless
/// <c>MINTOKEI_SANDBOX_DOCKER_ITEST=1</c>, so normal CI never runs it. Uses the .NET SDK image as a
/// product-agnostic, git-capable workload.
/// </summary>
public class SandboxBrokerE2ETests
{
    private const string Workload = "mcr.microsoft.com/dotnet/sdk:10.0"; // has git; already present in the dev/CI cache
    private const string AllowedClone = "git clone --depth 1 https://github.com/octocat/Hello-World.git /tmp/r";
    private const string DeniedClone = "git clone --depth 1 https://gitlab.com/gitlab-org/gitlab-test.git /tmp/r";

    [Fact]
    public async Task Broker_allows_allowlisted_egress_blocks_the_rest_and_denies_direct()
    {
        if (Environment.GetEnvironmentVariable("MINTOKEI_SANDBOX_DOCKER_ITEST") != "1")
            Assert.Skip("opt-in only: set MINTOKEI_SANDBOX_DOCKER_ITEST=1 (needs Docker + internet) to run the broker e2e");
        var root = FindRepoRoot();
        if (root is null)
            Assert.Skip("could not locate the repo root (Dockerfile.broker) from the test output dir");

        var tag = "mintokei/sandbox-broker:itest";
        Assert.Equal(0, await Docker(["build", "-f", Path.Combine(root, "Dockerfile.broker"), "-t", tag, root], TimeSpan.FromMinutes(6)));

        var net = DockerNetwork.Name($"e2e-{Guid.NewGuid():N}"[..14]);
        var broker = $"mk-e2e-broker-{Guid.NewGuid():N}"[..24];
        Assert.Equal(0, await Docker(DockerNetwork.CreateArgs(net)));
        try
        {
            // Broker on the --internal net, allowlisting github.com; then also on bridge so IT (only) has egress.
            Assert.Equal(0, await Docker(["run", "-d", "--name", broker, "--network", net, "-e", "BROKER_ALLOW=github.com", tag]));
            Assert.Equal(0, await Docker(["network", "connect", "bridge", broker]));
            await Task.Delay(TimeSpan.FromSeconds(3)); // let the proxy bind

            string[] proxy = ["-e", $"https_proxy=http://{broker}:3128", "-e", $"http_proxy=http://{broker}:3128"];

            // Allowlisted host → clones THROUGH the broker (real end-to-end TLS clone).
            var allowed = await Docker(["run", "--rm", "--network", net, .. proxy, Workload, "sh", "-c", AllowedClone], TimeSpan.FromMinutes(2));
            Assert.Equal(0, allowed);

            // Non-allowlisted host → broker 403 → clone fails.
            var denied = await Docker(["run", "--rm", "--network", net, .. proxy, Workload, "sh", "-c", DeniedClone], TimeSpan.FromMinutes(1));
            Assert.NotEqual(0, denied);

            // No proxy → the --internal network has no route out → deny-by-default.
            var direct = await Docker(["run", "--rm", "--network", net, Workload, "sh", "-c", AllowedClone], TimeSpan.FromMinutes(1));
            Assert.NotEqual(0, direct);
        }
        finally
        {
            await Docker(["rm", "-f", broker]);
            await Docker(DockerNetwork.RemoveArgs(net));
        }
    }

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile.broker")))
                return dir.FullName;
        return null;
    }

    private static async Task<int> Docker(IReadOnlyList<string> args, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { /* already gone */ } return -1; }
        return p.ExitCode;
    }
}
