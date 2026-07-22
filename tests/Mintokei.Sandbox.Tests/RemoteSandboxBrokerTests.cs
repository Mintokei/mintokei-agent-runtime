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
        Assert.Contains("BROKER_MODEL_ANTHROPIC_UPSTREAM=https://api.anthropic.com", run); // legacy scalar → anthropic provider
        Assert.Contains("BROKER_MODEL_ANTHROPIC_PORT=3130", run);
        Assert.Contains("BROKER_MODEL_ANTHROPIC_AUTH=x-api-key=sk", run);
        Assert.Contains(fake.Calls, c => c.Contains("network") && c.Contains("connect") && c.Contains("bridge"));

        Assert.Equal("sbx-1-broker", e.ContainerName);
        Assert.Equal("http://sbx-1-broker:3128", e.ProxyUrl);
        Assert.Equal("http://sbx-1-broker:3129/git-credential", e.GitMintUrl);
        Assert.Equal("http://sbx-1-broker:3130", e.ModelUrls!["anthropic"]);
    }

    [Fact]
    public async Task StartAsync_with_multiple_providers_emits_a_group_and_a_distinct_port_per_provider()
    {
        var fake = new FakeRunner();
        var e = await New(fake).StartAsync(Guid.NewGuid(), new SandboxBrokerRequest(
            "sbx-1", ["api.anthropic.com", "api.openai.com"],
            new SandboxBrokerSecrets(ModelUpstreams:
            [
                new("anthropic", "https://api.anthropic.com", "Authorization: Bearer ant"),
                new("openai",    "https://api.openai.com",    "Authorization: Bearer oai"),
            ])));

        var run = fake.Calls.First(c => c.Contains("run"));
        Assert.Contains("BROKER_MODEL_ANTHROPIC_UPSTREAM=https://api.anthropic.com", run);
        Assert.Contains("BROKER_MODEL_ANTHROPIC_PORT=3130", run);
        Assert.Contains("BROKER_MODEL_ANTHROPIC_AUTH=Authorization: Bearer ant", run);
        Assert.Contains("BROKER_MODEL_OPENAI_UPSTREAM=https://api.openai.com", run);
        Assert.Contains("BROKER_MODEL_OPENAI_PORT=3131", run);
        Assert.Equal("http://sbx-1-broker:3130", e.ModelUrls!["anthropic"]);
        Assert.Equal("http://sbx-1-broker:3131", e.ModelUrls["openai"]);       // distinct port → one broker, two providers
    }

    [Fact]
    public async Task StartAsync_skips_unknown_providers()
    {
        var fake = new FakeRunner();
        var e = await New(fake).StartAsync(Guid.NewGuid(), new SandboxBrokerRequest(
            "sbx-2", ["x"], new SandboxBrokerSecrets(ModelUpstreams: [new("mystery", "https://api.example.com", "k=v")])));

        Assert.Null(e.ModelUrls);
        Assert.DoesNotContain(fake.Calls, c => c.Any(a => a.StartsWith("BROKER_MODEL_")));
    }

    [Fact]
    public async Task StartAsync_with_a_github_token_mints_it_for_copilot_and_never_seeds_it()
    {
        var fake = new FakeRunner();
        var e = await New(fake).StartAsync(Guid.NewGuid(), new SandboxBrokerRequest(
            "sbx-1", ["api.github.com"], new SandboxBrokerSecrets(GitHubToken: "gho_realtoken")));

        var run = fake.Calls.First(c => c.Contains("run"));
        Assert.Contains("BROKER_GITHUB_TOKEN=gho_realtoken", run);               // held broker-side
        Assert.Contains("BROKER_GITHUB_PORT=3132", run);
        Assert.Equal("http://sbx-1-broker:3132", e.GitHubApiUrl);               // Copilot's GitHub API points here
    }

    [Fact]
    public async Task StartAsync_without_model_leaves_model_urls_null_and_omits_model_env()
    {
        var fake = new FakeRunner();
        var e = await New(fake).StartAsync(Guid.NewGuid(), new SandboxBrokerRequest("sbx-3", ["github.com"]));

        Assert.Null(e.ModelUrls);
        Assert.Null(e.GitHubApiUrl);
        Assert.DoesNotContain(fake.Calls, c => c.Any(a => a.StartsWith("BROKER_MODEL_") || a.StartsWith("BROKER_GITHUB_")));
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
