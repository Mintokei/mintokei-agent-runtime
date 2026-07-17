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
        public HashSet<string> ExitedNames { get; } = [];    // per-name override (else Status)
        public List<SandboxHandle> Managed { get; } = [];    // returned by ListManagedAsync

        public string Backend => "fake";

        public Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            Provisioned.Add(spec);
            return Task.FromResult(new SandboxHandle($"id-{spec.Name}", spec.Name, Backend));
        }

        public Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct = default)
            => Task.FromResult(new SandboxStatus(ExitedNames.Contains(handle.Name) ? SandboxState.Exited : Status));

        public Task StopAsync(SandboxHandle handle, CancellationToken ct = default)
        {
            Stopped.Add(handle.Name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SandboxHandle>> ListManagedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SandboxHandle>>(Managed);
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

    [Fact]
    public async Task TryAcquireWarm_claims_a_matching_warm_sandbox_once()
    {
        var (manager, _) = NewManager(warmPoolSize: 2);
        var n = 0;
        await manager.MaintainWarmPoolAsync(_ => Task.FromResult(Request($"w{n++}")));

        var first = manager.TryAcquireWarm("standard");
        var second = manager.TryAcquireWarm("standard");
        var third = manager.TryAcquireWarm("standard");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Handle.Name, second!.Handle.Name); // two distinct sandboxes
        Assert.False(first.Warm);                                  // flipped to serving
        Assert.Null(third);                                        // pool exhausted
    }

    [Fact]
    public async Task TryAcquireWarm_returns_null_for_a_profile_with_no_warm_sandbox()
    {
        var (manager, _) = NewManager(warmPoolSize: 1);
        await manager.MaintainWarmPoolAsync(_ => Task.FromResult(Request("w0")));

        Assert.Null(manager.TryAcquireWarm("strict"));
        Assert.NotNull(manager.TryAcquireWarm("standard"));
    }

    [Fact]
    public async Task Reconcile_reaps_exited_containers_and_leaves_running_ones()
    {
        var (manager, runtime) = NewManager();
        runtime.Managed.Add(new SandboxHandle("id-running", "running-one", "fake"));
        runtime.Managed.Add(new SandboxHandle("id-exited", "exited-one", "fake"));
        runtime.ExitedNames.Add("exited-one");

        var reaped = await manager.ReconcileAsync();

        Assert.Equal(1, reaped);
        Assert.Contains("exited-one", runtime.Stopped);
        Assert.DoesNotContain("running-one", runtime.Stopped); // running container is left alone
    }
}
