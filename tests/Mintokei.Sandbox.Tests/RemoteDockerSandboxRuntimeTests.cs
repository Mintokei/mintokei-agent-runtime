using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts;
using Mintokei.Runner.Contracts.Messages;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Docker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class RemoteDockerSandboxRuntimeTests
{
    private sealed class FakeCommandRunner : IRemoteCommandRunner
    {
        public Func<IReadOnlyList<string>, RunCommandResponse>? Handler { get; set; }
        public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = [];

        public Task<RunCommandResponse> RunAsync(
            Guid machineId, string workingDirectory, string executable,
            IReadOnlyList<string> args, int timeoutMs, CancellationToken ct = default)
        {
            Calls.Add((executable, args));
            return Task.FromResult(Handler?.Invoke(args) ?? new RunCommandResponse("", 0, "", "", null));
        }
    }

    private static RemoteDockerSandboxRuntime New(FakeCommandRunner fake)
        => new(fake, Options.Create(new SandboxOptions()), NullLogger<RemoteDockerSandboxRuntime>.Instance);

    private static SandboxSpec Spec() => new()
    {
        Image = "mintokei/sandbox:latest",
        Name = "sess-1",
        RuntimeClass = "runc",
        Limits = new SandboxResourceLimits(1024, 1, 128),
        Args = ["--backend", "https://api", "--name", "sess-1"],
    };

    [Fact]
    public async Task Provision_dispatches_docker_run_and_returns_the_container_id()
    {
        var fake = new FakeCommandRunner
        {
            Handler = args => args[0] == "run" ? new RunCommandResponse("", 0, "container-abc123\n", "", null)
                                               : new RunCommandResponse("", 0, "", "", null),
        };

        var handle = await New(fake).ProvisionAsync(Guid.NewGuid(), Spec(), CancellationToken.None);

        Assert.Equal("container-abc123", handle.Id);
        Assert.Equal("docker-remote", handle.Backend);
        var run = Assert.Single(fake.Calls, c => c.Exe == "docker" && c.Args.Count > 0 && c.Args[0] == "run");
        Assert.Contains("mintokei/sandbox:latest", run.Args); // reuses DockerCommand.BuildRunArgs → image present
        Assert.Contains("--backend", run.Args);               // runner flags appended after the image
    }

    [Fact]
    public async Task Provision_throws_on_nonzero_exit()
    {
        var fake = new FakeCommandRunner { Handler = _ => new RunCommandResponse("", 125, "", "no such image", null) };
        await Assert.ThrowsAsync<SandboxRuntimeException>(
            () => New(fake).ProvisionAsync(Guid.NewGuid(), Spec(), CancellationToken.None));
    }

    [Fact]
    public async Task Status_parses_state_and_exit_code()
    {
        var fake = new FakeCommandRunner { Handler = _ => new RunCommandResponse("", 0, "exited 137", "", null) };
        var status = await New(fake).GetStatusAsync(
            Guid.NewGuid(), new SandboxHandle("id", "n", "docker-remote"), CancellationToken.None);
        Assert.Equal(SandboxState.Exited, status.State);
        Assert.Equal(137, status.ExitCode);
    }

    [Fact]
    public async Task Stop_dispatches_docker_rm_force()
    {
        var fake = new FakeCommandRunner();
        await New(fake).StopAsync(Guid.NewGuid(), new SandboxHandle("cid", "n", "docker-remote"), CancellationToken.None);
        var call = Assert.Single(fake.Calls);
        Assert.Equal("docker", call.Exe);
        Assert.Equal(new[] { "rm", "--force", "cid" }, call.Args);
    }

    [Fact]
    public async Task EnsureWorkspaceVolume_creates_a_labelled_volume()
    {
        var fake = new FakeCommandRunner();
        var taskId = Guid.NewGuid();
        var name = RemoteDockerSandboxRuntime.WorkspaceVolumeName(taskId);

        await New(fake).EnsureWorkspaceVolumeAsync(Guid.NewGuid(), name, taskId, CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("docker", call.Exe);
        Assert.Equal("volume", call.Args[0]);
        Assert.Equal("create", call.Args[1]);
        Assert.Contains(name, call.Args);
        Assert.Contains($"mintokei.task={taskId:N}", call.Args);
    }

    [Fact]
    public void WorkspaceVolumeName_round_trips_through_TryParse()
    {
        var id = Guid.NewGuid();
        Assert.True(RemoteDockerSandboxRuntime.TryParseWorkspaceTaskId(
            RemoteDockerSandboxRuntime.WorkspaceVolumeName(id), out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(RemoteDockerSandboxRuntime.TryParseWorkspaceTaskId("some-other-volume", out _));
    }
}
