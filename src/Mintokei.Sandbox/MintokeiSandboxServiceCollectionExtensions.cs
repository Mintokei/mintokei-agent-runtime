using Microsoft.Extensions.Configuration;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Docker;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the Mintokei sandbox layer: the container-runtime abstraction (<see cref="ISandboxRuntime"/>,
/// Docker impl), the isolation-profile resolver, and the spec factory. A host adds this, supplies
/// <see cref="SandboxOptions"/> (from the <c>Sandbox</c> config section or in code), and asks the factory
/// + runtime to provision sandboxes. The K8s backend registers here later without changing callers.
/// </summary>
public static class MintokeiSandboxServiceCollectionExtensions
{
    public static IServiceCollection AddMintokeiSandbox(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SandboxOptions>(configuration.GetSection(SandboxOptions.SectionName));
        return services.AddMintokeiSandboxCore();
    }

    public static IServiceCollection AddMintokeiSandbox(this IServiceCollection services, Action<SandboxOptions> configure)
    {
        services.Configure(configure);
        return services.AddMintokeiSandboxCore();
    }

    private static IServiceCollection AddMintokeiSandboxCore(this IServiceCollection services)
    {
        services.AddSingleton<SandboxProfileResolver>();
        services.AddSingleton<SandboxSpecFactory>();
        // One backend today; becomes keyed ("docker" | "k8s") when the K8s runtime lands.
        services.AddSingleton<ISandboxRuntime, DockerSandboxRuntime>();
        services.AddSingleton<SandboxManager>();
        return services;
    }
}
