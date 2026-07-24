using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>The secrets seam: in broker mode <see cref="SandboxManager"/> resolves the product's
/// <see cref="ISandboxBrokerSecretsProvider"/> and puts the result on the spec (which the K8s runtime reads);
/// non-broker profiles never touch it, so the product can't accidentally source secrets for an open sandbox.</summary>
public class SandboxBrokerSecretsProviderTests
{
    private sealed class CapturingRuntime : ISandboxRuntime
    {
        public SandboxSpec? Last;
        public string Backend => "fake";
        public Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default)
        { Last = spec; return Task.FromResult(new SandboxHandle("id", spec.Name, Backend)); }
        public Task<SandboxStatus> GetStatusAsync(SandboxHandle h, CancellationToken ct = default)
            => Task.FromResult(new SandboxStatus(SandboxState.Running));
        public Task StopAsync(SandboxHandle h, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SandboxHandle>> ListManagedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SandboxHandle>>([]);
    }

    private sealed class StubProvider(SandboxBrokerSecrets? secrets) : ISandboxBrokerSecretsProvider
    {
        public int Calls;
        public Task<SandboxBrokerSecrets?> ResolveAsync(SandboxSessionRequest r, SandboxProfile p, CancellationToken ct = default)
        { Calls++; return Task.FromResult(secrets); }
    }

    private static SandboxManager Manager(CapturingRuntime runtime, ISandboxBrokerSecretsProvider provider, string egress)
    {
        var options = Options.Create(new SandboxOptions
        {
            Image = "img:1",
            DefaultProfile = "p",
            AllowedProfiles = ["p"],
            Profiles = { ["p"] = new SandboxProfileConfig { Egress = egress, EgressAllowlist = ["api.anthropic.com"] } },
        });
        return new SandboxManager(runtime, new SandboxProfileResolver(options), new SandboxSpecFactory(options),
            options, NullLogger<SandboxManager>.Instance, provider);
    }

    private static SandboxSessionRequest Request() => new() { BackendUrl = "https://api", EnrollmentToken = "tok", Name = "s1" };

    [Fact]
    public async Task Broker_profile_resolves_the_providers_secrets_onto_the_spec()
    {
        var runtime = new CapturingRuntime();
        var secrets = new SandboxBrokerSecrets().WithModel(ModelUpstreamSpec.AnthropicOAuth("sk-ant-oat-XYZ"));
        var provider = new StubProvider(secrets);

        await Manager(runtime, provider, egress: "broker").ProvisionAsync(Request());

        Assert.Equal(1, provider.Calls);
        Assert.Same(secrets, runtime.Last!.BrokerSecrets); // the K8s runtime reads exactly this
    }

    [Fact]
    public async Task Non_broker_profile_never_calls_the_provider()
    {
        var runtime = new CapturingRuntime();
        var provider = new StubProvider(new SandboxBrokerSecrets());

        await Manager(runtime, provider, egress: "open").ProvisionAsync(Request());

        Assert.Equal(0, provider.Calls);
        Assert.Null(runtime.Last!.BrokerSecrets);
    }

    [Fact]
    public async Task Default_no_op_provider_leaves_broker_secrets_null()
    {
        var runtime = new CapturingRuntime();

        await Manager(runtime, new NoSandboxBrokerSecrets(), egress: "broker").ProvisionAsync(Request());

        Assert.Null(runtime.Last!.BrokerSecrets); // containment still enforced downstream; just nothing injected
    }
}
