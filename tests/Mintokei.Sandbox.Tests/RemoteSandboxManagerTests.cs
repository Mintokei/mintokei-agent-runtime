using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts;
using Mintokei.Runner.Contracts.Messages;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Docker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class RemoteSandboxManagerTests
{
    private sealed class FakeCommandRunner : IRemoteCommandRunner
    {
        public Func<string, IReadOnlyList<string>, RunCommandResponse> Handler { get; set; } = (_, _) => new("", 0, "", "", null);
        public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = [];

        public Task<RunCommandResponse> RunAsync(
            Guid machineId, string workingDirectory, string executable,
            IReadOnlyList<string> args, int timeoutMs, CancellationToken ct = default)
        {
            Calls.Add((executable, args));
            return Task.FromResult(Handler(executable, args));
        }
    }

    private static (RemoteSandboxManager Mgr, FakeCommandRunner Fake) New(FakeCommandRunner fake)
    {
        var opts = Options.Create(new SandboxOptions { Image = "img:1", DefaultProfile = "standard", AllowedProfiles = ["standard"] });
        var runtime = new RemoteDockerSandboxRuntime(fake, opts, NullLogger<RemoteDockerSandboxRuntime>.Instance);
        var stager = new SandboxCredentialStager(fake, opts);
        var mgr = new RemoteSandboxManager(runtime, stager, new SandboxSpecFactory(opts),
            new SandboxProfileResolver(opts), NullLogger<RemoteSandboxManager>.Instance);
        return (mgr, fake);
    }

    private static SandboxSessionRequest Request() => new()
    {
        BackendUrl = "https://api",
        EnrollmentToken = "tok",
        Name = "sbx-1",
        ClaudeConfigHostDir = "/root/.claude",
    };

    // version → "24.0.0" (probe ok); sh → a STAGED marker; docker run → a container id.
    private static FakeCommandRunner HappyRunner() => new()
    {
        Handler = (exe, args) =>
            exe == "sh" ? new("", 0, "STAGED .claude\n", "", null)
            : args.Count > 0 && args[0] == "run" ? new("", 0, "container-abc\n", "", null)
            : new("", 0, "24.0.0", "", null),
    };

    [Fact]
    public async Task Launch_probes_stages_provisions_and_returns_online_session()
    {
        var (mgr, fake) = New(HappyRunner());
        var machineId = Guid.NewGuid();

        await using var s = await mgr.LaunchAsync(Guid.NewGuid(), machineId, Request(), _ => true);

        Assert.Equal(machineId, s.MachineId);
        Assert.Equal("container-abc", s.Handle.Id);
        Assert.Contains(fake.Calls, c => c.Exe == "sh");                                              // creds staged
        Assert.Contains(fake.Calls, c => c.Exe == "docker" && c.Args.Count > 0 && c.Args[0] == "run"); // provisioned
    }

    [Fact]
    public async Task Launch_recycles_and_throws_when_container_exits_before_online()
    {
        var fake = new FakeCommandRunner
        {
            Handler = (exe, args) =>
                exe == "sh" ? new("", 0, "STAGED .claude\n", "", null)
                : args.Count > 0 && args[0] == "run" ? new("", 0, "cid\n", "", null)
                : args.Count > 0 && args[0] == "inspect" ? new("", 0, "exited 1", "", null)
                : args.Count > 0 && args[0] == "logs" ? new("", 0, "clone failed", "", null)
                : new("", 0, "24.0.0", "", null),
        };
        var (mgr, _) = New(fake);

        var ex = await Assert.ThrowsAsync<SandboxRuntimeException>(
            () => mgr.LaunchAsync(Guid.NewGuid(), Guid.NewGuid(), Request(), _ => false));

        Assert.Contains("exited before its runner connected", ex.Message);
        Assert.Contains("clone failed", ex.Message);                                                 // logs surfaced
        Assert.Contains(fake.Calls, c => c.Exe == "docker" && c.Args.Count > 0 && c.Args[0] == "rm"); // recycled: docker rm
        Assert.Contains(fake.Calls, c => c.Exe == "rm");                                              // recycled: staged creds
    }

    [Fact]
    public async Task Dispose_recycles_the_container_and_staged_creds()
    {
        var (mgr, fake) = New(HappyRunner());
        var s = await mgr.LaunchAsync(Guid.NewGuid(), Guid.NewGuid(), Request(), _ => true);
        fake.Calls.Clear();

        await s.DisposeAsync();

        Assert.Contains(fake.Calls, c => c.Exe == "docker" && c.Args.Count >= 2 && c.Args[0] == "rm" && c.Args[1] == "--force");
        Assert.Contains(fake.Calls, c => c.Exe == "rm" && c.Args.Contains("-rf"));
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var (mgr, fake) = New(HappyRunner());
        var s = await mgr.LaunchAsync(Guid.NewGuid(), Guid.NewGuid(), Request(), _ => true);
        fake.Calls.Clear();

        await s.DisposeAsync();
        var afterFirst = fake.Calls.Count;
        await s.DisposeAsync();                              // second dispose is a no-op

        Assert.Equal(afterFirst, fake.Calls.Count);
    }

    // ── Broker egress ──────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeBroker : ISandboxBroker
    {
        public int Started;
        public int Stopped;
        public BrokerEndpoint Endpoint { get; set; } =
            new("net-x", "sbx-1-broker", "http://sbx-1-broker:3128", "http://sbx-1-broker:3129/git-credential", null);

        public Task<BrokerEndpoint> StartAsync(Guid workerId, SandboxBrokerRequest request, CancellationToken ct = default)
        { Started++; return Task.FromResult(Endpoint); }

        public Task StopAsync(Guid workerId, BrokerEndpoint endpoint, CancellationToken ct = default)
        { Stopped++; return Task.CompletedTask; }
    }

    private static SandboxOptions BrokerOptions() => new()
    {
        Image = "img:1",
        DefaultProfile = "standard",
        AllowedProfiles = ["standard", "hardened"],
        Profiles =
        {
            ["standard"] = new SandboxProfileConfig(),
            ["hardened"] = new SandboxProfileConfig { Egress = "broker", EgressAllowlist = ["github.com"] },
        },
    };

    // version → ok; docker run → a container id (no "sh" staging expected in broker mode).
    private static FakeCommandRunner BrokerRunner() => new()
    {
        Handler = (_, args) => args.Count > 0 && args[0] == "run" ? new("", 0, "cid\n", "", null) : new("", 0, "24.0.0", "", null),
    };

    private static (RemoteSandboxManager Mgr, FakeCommandRunner Fake, FakeBroker Broker) NewBroker(FakeCommandRunner fake)
    {
        var opts = Options.Create(BrokerOptions());
        var runtime = new RemoteDockerSandboxRuntime(fake, opts, NullLogger<RemoteDockerSandboxRuntime>.Instance);
        var broker = new FakeBroker();
        var mgr = new RemoteSandboxManager(runtime, new SandboxCredentialStager(fake, opts),
            new SandboxSpecFactory(opts), new SandboxProfileResolver(opts), NullLogger<RemoteSandboxManager>.Instance, broker);
        return (mgr, fake, broker);
    }

    [Fact]
    public async Task Launch_in_broker_mode_starts_the_broker_skips_staging_and_joins_its_internal_network()
    {
        var (mgr, fake, broker) = NewBroker(BrokerRunner());

        await using var s = await mgr.LaunchAsync(Guid.NewGuid(), Guid.NewGuid(), Request(), _ => true,
            profile: "hardened", brokerSecrets: new SandboxBrokerSecrets(GitCredentials: "github.com=x:tok"));

        Assert.Equal(1, broker.Started);
        Assert.DoesNotContain(fake.Calls, c => c.Exe == "sh");                 // NO credential staging in broker mode
        var run = fake.Calls.First(c => c.Exe == "docker" && c.Args.Count > 0 && c.Args[0] == "run").Args;
        Assert.Contains("--network", run);
        Assert.Contains("net-x", run);                                          // joined the broker's --internal net
        Assert.Contains("NO_PROXY=sbx-1-broker", run);                          // broker host bypasses its own CONNECT proxy (plaintext git-mint/model)
        Assert.DoesNotContain(run, a => a.StartsWith("ANTHROPIC_AUTH_TOKEN=")); // no model injection here → no placeholder credential
    }

    [Fact]
    public async Task Launch_in_broker_mode_with_model_injection_points_that_providers_cli_at_the_broker_with_a_placeholder()
    {
        var (mgr, fake, broker) = NewBroker(BrokerRunner());
        broker.Endpoint = broker.Endpoint with                                  // broker holds the real key
        {
            ModelUrls = new Dictionary<string, string> { ["anthropic"] = "http://sbx-1-broker:3130" },
        };

        await using var s = await mgr.LaunchAsync(Guid.NewGuid(), Guid.NewGuid(), Request(), _ => true,
            profile: "hardened", brokerSecrets: new SandboxBrokerSecrets(ModelUpstream: "https://api.anthropic.com"));

        var run = fake.Calls.First(c => c.Exe == "docker" && c.Args.Count > 0 && c.Args[0] == "run").Args;
        Assert.Contains("ANTHROPIC_BASE_URL=http://sbx-1-broker:3130", run);     // CLI talks to the broker, not the API
        // A placeholder credential so the CLI ATTEMPTS the call (else it refuses / prompts login); the broker
        // replaces the auth header with the real key, so the sandbox never holds a real credential.
        Assert.Contains("ANTHROPIC_AUTH_TOKEN=mintokei-broker-injects-the-real-credential", run);
        // A provider that ISN'T configured gets neither its base URL redirected nor a placeholder.
        Assert.DoesNotContain(run, a => a.StartsWith("OPENAI_BASE_URL="));
        Assert.DoesNotContain(run, a => a.StartsWith("OPENAI_API_KEY="));
    }

    [Fact]
    public async Task Launch_in_broker_mode_with_two_providers_points_each_cli_at_its_own_broker_port()
    {
        var (mgr, fake, broker) = NewBroker(BrokerRunner());
        broker.Endpoint = broker.Endpoint with
        {
            ModelUrls = new Dictionary<string, string>
            {
                ["anthropic"] = "http://sbx-1-broker:3130",
                ["openai"] = "http://sbx-1-broker:3131",
            },
        };

        await using var s = await mgr.LaunchAsync(Guid.NewGuid(), Guid.NewGuid(), Request(), _ => true,
            profile: "hardened", brokerSecrets: new SandboxBrokerSecrets(ModelUpstreams:
            [
                new("anthropic", "https://api.anthropic.com", "Authorization: Bearer ant"),
                new("openai", "https://api.openai.com", "Authorization: Bearer oai"),
            ]));

        var run = fake.Calls.First(c => c.Exe == "docker" && c.Args.Count > 0 && c.Args[0] == "run").Args;
        Assert.Contains("ANTHROPIC_BASE_URL=http://sbx-1-broker:3130", run);     // each provider → its own port
        Assert.Contains("OPENAI_BASE_URL=http://sbx-1-broker:3131", run);
        Assert.Contains("ANTHROPIC_AUTH_TOKEN=mintokei-broker-injects-the-real-credential", run);
        Assert.Contains("OPENAI_API_KEY=mintokei-broker-injects-the-real-credential", run);
    }

    [Fact]
    public async Task Launch_in_broker_mode_with_a_github_token_points_copilot_at_the_broker_with_a_placeholder()
    {
        var (mgr, fake, broker) = NewBroker(BrokerRunner());
        broker.Endpoint = broker.Endpoint with { GitHubApiUrl = "http://sbx-1-broker:3132" };

        await using var s = await mgr.LaunchAsync(Guid.NewGuid(), Guid.NewGuid(), Request(), _ => true,
            profile: "hardened", brokerSecrets: new SandboxBrokerSecrets(GitHubToken: "gho_realtoken"));

        var run = fake.Calls.First(c => c.Exe == "docker" && c.Args.Count > 0 && c.Args[0] == "run").Args;
        Assert.Contains("COPILOT_DEBUG_GITHUB_API_URL=http://sbx-1-broker:3132", run);     // Copilot's GitHub API → broker
        Assert.Contains(run, a => a.StartsWith("COPILOT_GITHUB_TOKEN=github_pat_"));        // format-valid placeholder
        Assert.DoesNotContain(run, a => a.Contains("gho_realtoken"));                       // the REAL token never reaches the box
    }

    [Fact]
    public async Task Dispose_in_broker_mode_stops_the_broker_not_the_stager()
    {
        var (mgr, fake, broker) = NewBroker(BrokerRunner());
        var s = await mgr.LaunchAsync(Guid.NewGuid(), Guid.NewGuid(), Request(), _ => true, profile: "hardened");
        fake.Calls.Clear();

        await s.DisposeAsync();

        Assert.Equal(1, broker.Stopped);                                        // broker torn down
        Assert.DoesNotContain(fake.Calls, c => c.Exe == "rm" && c.Args.Contains("-rf")); // NOT the stager's rm -rf
        Assert.Contains(fake.Calls, c => c.Exe == "docker" && c.Args.Count >= 2 && c.Args[0] == "rm" && c.Args[1] == "--force");
    }

    [Fact]
    public async Task Launch_in_broker_mode_without_a_registered_broker_fails_closed()
    {
        var opts = Options.Create(BrokerOptions());
        var fake = BrokerRunner();
        var runtime = new RemoteDockerSandboxRuntime(fake, opts, NullLogger<RemoteDockerSandboxRuntime>.Instance);
        var mgr = new RemoteSandboxManager(runtime, new SandboxCredentialStager(fake, opts),
            new SandboxSpecFactory(opts), new SandboxProfileResolver(opts), NullLogger<RemoteSandboxManager>.Instance); // no broker

        var ex = await Assert.ThrowsAsync<SandboxRuntimeException>(() =>
            mgr.LaunchAsync(Guid.NewGuid(), Guid.NewGuid(), Request(), _ => true, profile: "hardened"));
        Assert.Contains("fail-closed", ex.Message);
    }
}
