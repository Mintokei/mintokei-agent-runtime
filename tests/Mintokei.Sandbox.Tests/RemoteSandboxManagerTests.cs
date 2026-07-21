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
}
