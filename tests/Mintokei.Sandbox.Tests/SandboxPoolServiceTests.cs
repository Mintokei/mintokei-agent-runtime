using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class SandboxPoolServiceTests
{
    private sealed class FakeRuntime : ISandboxRuntime
    {
        public int Provisioned;
        public string Backend => "fake";

        public Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            Provisioned++;
            return Task.FromResult(new SandboxHandle($"id-{spec.Name}", spec.Name, Backend));
        }

        public Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct = default)
            => Task.FromResult(new SandboxStatus(SandboxState.Running));

        public Task StopAsync(SandboxHandle handle, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SandboxHandle>> ListManagedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SandboxHandle>>([]);
    }

    private sealed class CountingSource : ISandboxSessionSource
    {
        public int Calls;

        public Task<SandboxSessionRequest> CreateWarmRequestAsync(CancellationToken ct = default)
        {
            var n = ++Calls;
            return Task.FromResult(new SandboxSessionRequest
            {
                BackendUrl = "https://api",
                EnrollmentToken = "tok",
                Name = $"w{n}",
            });
        }
    }

    private static (SandboxPoolService Service, FakeRuntime Runtime, CountingSource Source) New(int warmPoolSize)
    {
        var options = Options.Create(new SandboxOptions
        {
            Image = "img",
            WarmPoolSize = warmPoolSize,
            AllowedProfiles = ["standard"],
            Profiles = { ["standard"] = new SandboxProfileConfig() },
        });
        var runtime = new FakeRuntime();
        var manager = new SandboxManager(
            runtime, new SandboxProfileResolver(options), new SandboxSpecFactory(options), options,
            NullLogger<SandboxManager>.Instance, new NoSandboxBrokerSecrets());
        var source = new CountingSource();
        var service = new SandboxPoolService(manager, source, options, NullLogger<SandboxPoolService>.Instance);
        return (service, runtime, source);
    }

    [Fact]
    public async Task RunOnce_tops_warm_pool_from_the_session_source()
    {
        var (service, runtime, source) = New(warmPoolSize: 3);

        await service.RunOnceAsync();

        Assert.Equal(3, source.Calls);
        Assert.Equal(3, runtime.Provisioned);
    }

    [Fact]
    public async Task Disabled_pool_provisions_nothing()
    {
        var (service, runtime, _) = New(warmPoolSize: 0);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, runtime.Provisioned);
    }
}
