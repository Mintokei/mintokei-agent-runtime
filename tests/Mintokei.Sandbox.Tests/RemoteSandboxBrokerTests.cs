using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts;
using Mintokei.Runner.Contracts.Messages;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Docker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class RemoteSandboxBrokerTests
{
    private sealed class FakeRunner : IRemoteCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];                       // each = [executable, ...args]
        public Func<IReadOnlyList<string>, RunCommandResponse> Handler { get; set; } = _ => new("", 0, "", "", null);

        public Task<RunCommandResponse> RunAsync(
            Guid machineId, string workingDirectory, string executable, IReadOnlyList<string> args, int timeoutMs, CancellationToken ct = default)
        {
            Calls.Add([executable, .. args]);
            return Task.FromResult(Handler(args));
        }
    }

    private static RemoteSandboxBroker New(FakeRunner fake) =>
        new(fake, Options.Create(new SandboxOptions { BrokerImage = "brk:1", BrokerEgressNetwork = "bridge" }),
            NullLogger<RemoteSandboxBroker>.Instance);

    [Fact]
    public async Task StartAsync_creates_network_runs_broker_with_env_and_attaches_egress()
    {
        var fake = new FakeRunner();

        var e = await New(fake).StartAsync(Guid.NewGuid(), new SandboxBrokerRequest(
            "sbx-1", ["github.com", "api.anthropic.com"],
            new SandboxBrokerSecrets(GitCredentials: "github.com=x:tok", ModelUpstream: "https://api.anthropic.com", ModelAuth: "x-api-key=sk")));

        Assert.Contains(fake.Calls, c => c.Contains("network") && c.Contains("create") && c.Contains("--internal"));
        var run = fake.Calls.First(c => c.Contains("run"));
        Assert.Contains("brk:1", run);
        Assert.Contains("BROKER_ALLOW=github.com,api.anthropic.com", run);
        Assert.Contains("BROKER_GIT_CREDS=github.com=x:tok", run);
        Assert.Contains("BROKER_MODEL_UPSTREAM=https://api.anthropic.com", run);
        Assert.Contains("BROKER_MODEL_AUTH=x-api-key=sk", run);
        Assert.Contains(fake.Calls, c => c.Contains("network") && c.Contains("connect") && c.Contains("bridge"));

        Assert.Equal("sbx-1-broker", e.ContainerName);
        Assert.Equal("http://sbx-1-broker:3128", e.ProxyUrl);
        Assert.Equal("http://sbx-1-broker:3129/git-credential", e.GitMintUrl);
        Assert.Equal("http://sbx-1-broker:3130", e.ModelUrl);
    }

    [Fact]
    public async Task StartAsync_without_model_leaves_model_url_null_and_omits_model_env()
    {
        var fake = new FakeRunner();
        var e = await New(fake).StartAsync(Guid.NewGuid(), new SandboxBrokerRequest("sbx-2", ["github.com"]));

        Assert.Null(e.ModelUrl);
        Assert.DoesNotContain(fake.Calls, c => c.Any(a => a.StartsWith("BROKER_MODEL_UPSTREAM")));
    }

    [Fact]
    public async Task StartAsync_fails_closed_and_removes_network_when_the_run_fails()
    {
        var fake = new FakeRunner { Handler = args => args.Contains("run") ? new("", 1, "", "boom", null) : new("", 0, "", "", null) };

        await Assert.ThrowsAsync<SandboxRuntimeException>(() =>
            New(fake).StartAsync(Guid.NewGuid(), new SandboxBrokerRequest("sbx-3", ["github.com"])));

        Assert.Contains(fake.Calls, c => c.Contains("network") && c.Contains("rm")); // cleaned up
    }

    [Fact]
    public async Task StopAsync_removes_the_container_and_the_network()
    {
        var fake = new FakeRunner();
        await New(fake).StopAsync(Guid.NewGuid(), new BrokerEndpoint("net-x", "sbx-broker", "", "", null));

        Assert.Contains(fake.Calls, c => c.Contains("rm") && c.Contains("--force") && c.Contains("sbx-broker"));
        Assert.Contains(fake.Calls, c => c.Contains("network") && c.Contains("rm") && c.Contains("net-x"));
    }
}
