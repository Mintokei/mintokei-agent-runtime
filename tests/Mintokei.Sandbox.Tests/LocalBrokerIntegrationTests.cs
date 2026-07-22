using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Docker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// Proves the WORKER-FREE broker path: <see cref="RemoteSandboxBroker"/> driven by <see cref="LocalCommandRunner"/>
/// creates the <c>--internal</c> network + broker container on THIS machine's Docker (no enrolled worker), and
/// tears both down. Opt-in only (needs Docker + the broker image) — skipped unless
/// <c>MINTOKEI_SANDBOX_DOCKER_ITEST=1</c>. Assumes the broker image tag <c>mintokei/sandbox-broker:test</c>
/// exists (build it with <c>docker build -f Dockerfile.broker -t mintokei/sandbox-broker:test .</c>).
/// </summary>
public class LocalBrokerIntegrationTests
{
    [Fact]
    public async Task Local_broker_starts_and_stops_on_this_machine_without_a_worker()
    {
        if (Environment.GetEnvironmentVariable("MINTOKEI_SANDBOX_DOCKER_ITEST") != "1")
            Assert.Skip("opt-in only: set MINTOKEI_SANDBOX_DOCKER_ITEST=1 (needs Docker + the broker image) to run");

        var local = new LocalCommandRunner();
        if ((await local.RunAsync(Guid.NewGuid(), "/", "docker", ["image", "inspect", "mintokei/sandbox-broker:test"], 15_000)).ExitCode != 0)
            Assert.Skip("broker image mintokei/sandbox-broker:test not present — build Dockerfile.broker first");

        var opts = Options.Create(new SandboxOptions { BrokerImage = "mintokei/sandbox-broker:test", BrokerEgressNetwork = "bridge" });
        var broker = new RemoteSandboxBroker(local, opts, NullLogger<RemoteSandboxBroker>.Instance);
        var session = $"litest-{Guid.NewGuid():N}"[..16];

        var endpoint = await broker.StartAsync(Guid.Empty, new SandboxBrokerRequest(session, ["github.com"]));
        try
        {
            // The broker container is actually running on the local daemon.
            var status = await local.RunAsync(Guid.NewGuid(), "/", "docker",
                ["inspect", "--format", "{{.State.Running}}", endpoint.ContainerName], 15_000);
            Assert.Equal(0, status.ExitCode);
            Assert.Equal("true", status.Stdout.Trim());
        }
        finally
        {
            await broker.StopAsync(Guid.Empty, endpoint);
        }

        // After stop, the container is gone.
        var gone = await local.RunAsync(Guid.NewGuid(), "/", "docker", ["inspect", endpoint.ContainerName], 15_000);
        Assert.NotEqual(0, gone.ExitCode);
    }
}
