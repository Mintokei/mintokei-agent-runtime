using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mintokei.Runner.Host.Server;
using Xunit;

namespace Mintokei.Sandbox.Hosting.Tests;

/// <summary>
/// Container-level checks on <c>AddSandboxAgentHost</c>. These exist because the unit tests construct the
/// types directly and so can't see lifetime mistakes: <see cref="IRunnerEnrollment"/> is scoped, and a
/// singleton that captured it would resolve fine in a unit test and throw in a real app.
/// </summary>
public class SandboxAgentHostRegistrationTests
{
    private static ServiceProvider BuildContainer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sandbox:BackendUrl"] = "https://backend/api",
            ["Sandbox:Backend"] = "docker",
        });
        builder.AddSandboxAgentHost().AddClaude();

        // Scope validation is what catches a singleton capturing a scoped dependency.
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    [Fact]
    public void AddSandboxAgentHost_resolves_the_host_without_capturing_a_scoped_dependency()
    {
        using var services = BuildContainer();

        // Resolved from the ROOT scope on purpose: that is where a captive scoped dependency throws.
        Assert.NotNull(services.GetRequiredService<SandboxAgentHost>());
        Assert.NotNull(services.GetRequiredService<SandboxProvisioner>());
    }

    [Fact]
    public void AddSandboxAgentHost_registers_the_sandbox_and_transport_layers()
    {
        using var services = BuildContainer();
        using var scope = services.CreateScope();

        Assert.NotNull(services.GetRequiredService<SandboxManager>());          // isolation half
        Assert.NotNull(services.GetRequiredService<ISandboxRuntime>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRunnerEnrollment>()); // transport half (scoped)
    }
}
