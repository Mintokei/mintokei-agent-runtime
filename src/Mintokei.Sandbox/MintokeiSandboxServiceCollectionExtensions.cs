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

    /// <summary>
    /// Registers the warm-pool host (<see cref="SandboxPoolService"/>) that drives top-up + reap on a timer.
    /// The embedder must also register an <see cref="ISandboxSessionSource"/> (mint token, backend URL,
    /// per-session policy). Dormant unless <see cref="SandboxOptions.WarmPoolSize"/> &gt; 0.
    /// </summary>
    public static IServiceCollection AddMintokeiSandboxPool(this IServiceCollection services)
    {
        services.AddHostedService<SandboxPoolService>();
        return services;
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
