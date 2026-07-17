using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class SandboxManagerTests
{
    private sealed class FakeRuntime : ISandboxRuntime
    {
        public List<SandboxSpec> Provisioned { get; } = [];
        public List<string> Stopped { get; } = [];
        public SandboxState Status { get; set; } = SandboxState.Running;

        public string Backend => "fake";

        public Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            Provisioned.Add(spec);
            return Task.FromResult(new SandboxHandle($"id-{spec.Name}", spec.Name, Backend));
        }

        public Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct = default)
            => Task.FromResult(new SandboxStatus(Status));

        public Task StopAsync(SandboxHandle handle, CancellationToken ct = default)
        {
            Stopped.Add(handle.Name);
            return Task.CompletedTask;
        }
    }

    private static (SandboxManager Manager, FakeRuntime Runtime) NewManager(int warmPoolSize = 0)
    {
        var options = Options.Create(new SandboxOptions
        {
            Image = "img:1",
            WarmPoolSize = warmPoolSize,
            AllowedProfiles = ["standard"],
            Profiles = { ["standard"] = new SandboxProfileConfig() },
        });
        var runtime = new FakeRuntime();
        var manager = new SandboxManager(
            runtime,
            new SandboxProfileResolver(options),
            new SandboxSpecFactory(options),
            options,
            NullLogger<SandboxManager>.Instance);
        return (manager, runtime);
    }

    private static SandboxSessionRequest Request(string name = "s1")
        => new() { BackendUrl = "https://api", EnrollmentToken = "tok", Name = name };

    [Fact]
    public async Task Provision_tracks_a_lease()
    {
        var (manager, runtime) = NewManager();

        var lease = await manager.ProvisionAsync(Request());

        Assert.Single(runtime.Provisioned);
        Assert.Single(manager.Active);
        Assert.Equal("standard", lease.Profile);
    }

    [Fact]
    public async Task Recycle_stops_and_untracks()
    {
        var (manager, runtime) = NewManager();
        await manager.ProvisionAsync(Request());

        await manager.RecycleAsync("s1");

        Assert.Empty(manager.Active);
        Assert.Contains("s1", runtime.Stopped);
    }

    [Fact]
    public async Task Warm_pool_tops_up_to_target()
    {
        var (manager, _) = NewManager(warmPoolSize: 3);
        var n = 0;

        await manager.MaintainWarmPoolAsync(_ => Task.FromResult(Request($"w{n++}")));

        Assert.Equal(3, manager.Active.Count);
        Assert.All(manager.Active, l => Assert.True(l.Warm));
    }

    [Fact]
    public async Task Reap_removes_exited_sandboxes()
    {
        var (manager, runtime) = NewManager();
        await manager.ProvisionAsync(Request());
        runtime.Status = SandboxState.Exited;

        var reaped = await manager.ReapAsync();

        Assert.Equal(1, reaped);
        Assert.Empty(manager.Active);
        Assert.Contains("s1", runtime.Stopped);
    }
}
