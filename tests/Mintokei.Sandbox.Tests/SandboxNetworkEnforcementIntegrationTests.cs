using System.Diagnostics;
using Mintokei.Sandbox.Docker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// Proves the <see cref="SandboxEgress.Broker"/> enforcement primitive against a REAL Docker daemon: a
/// container joined to the per-session <c>--internal</c> network (built from <see cref="DockerNetwork.CreateArgs"/>)
/// cannot reach the internet, while the same probe on the default bridge can. Opt-in only — skipped unless
/// <c>MINTOKEI_SANDBOX_DOCKER_ITEST=1</c> and the docker CLI works — so normal CI never runs it. This is what
/// proves the deny-by-default claim, not just that the arg-builder produces the right strings.
/// </summary>
public class SandboxNetworkEnforcementIntegrationTests
{
    // wget a raw public IP (no DNS dependency), short timeout; exit 0 only if the packet actually egressed.
    private const string Probe = "wget -T3 -qO- http://1.1.1.1 >/dev/null 2>&1";

    [Fact]
    public async Task Internal_network_denies_egress_while_default_bridge_allows_it()
    {
        if (!DockerAvailableAndOptedIn(out var reason))
            Assert.Skip(reason);

        var net = DockerNetwork.Name($"itest-{Guid.NewGuid():N}"[..18]);
        Assert.Equal(0, await Docker(DockerNetwork.CreateArgs(net))); // network created

        try
        {
            // On the --internal network: no route out → the probe fails (deny-by-default).
            var onInternal = await Docker(["run", "--rm", "--network", net, "alpine", "sh", "-c", Probe]);
            Assert.NotEqual(0, onInternal);

            // Control: the same probe on the default bridge succeeds → the block above is the network, not the host.
            var onBridge = await Docker(["run", "--rm", "alpine", "sh", "-c", Probe]);
            Assert.Equal(0, onBridge);
        }
        finally
        {
            await Docker(DockerNetwork.RemoveArgs(net)); // best-effort cleanup
        }
    }

    private static async Task<int> Docker(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        return p.ExitCode;
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
