using k8s;
using Microsoft.Extensions.Configuration;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Docker;
using Mintokei.Sandbox.Kubernetes;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the Mintokei sandbox layer: the container-runtime abstraction (<see cref="ISandboxRuntime"/>),
/// the isolation-profile resolver, and the spec factory. A host adds this, supplies
/// <see cref="SandboxOptions"/> (from the <c>Sandbox</c> config section or in code), and asks the factory
/// + runtime to provision sandboxes. The concrete backend is chosen from <see cref="SandboxOptions.Backend"/>
/// ("docker" | "kubernetes") — one backend per host process; the pool/lifecycle/reaper above the seam
/// don't know which is live.
/// </summary>
public static class MintokeiSandboxServiceCollectionExtensions
{
    public static IServiceCollection AddMintokeiSandbox(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SandboxOptions>(configuration.GetSection(SandboxOptions.SectionName));
        var backend = configuration[$"{SandboxOptions.SectionName}:Backend"];
        return services.AddMintokeiSandboxCore(backend);
    }

    public static IServiceCollection AddMintokeiSandbox(this IServiceCollection services, Action<SandboxOptions> configure)
    {
        services.Configure(configure);
        // Read the backend the caller selected (registration-time decision) without a built provider.
        var probe = new SandboxOptions();
        configure(probe);
        return services.AddMintokeiSandboxCore(probe.Backend);
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

    private static IServiceCollection AddMintokeiSandboxCore(this IServiceCollection services, string? backend)
    {
        services.AddSingleton<SandboxProfileResolver>();
        services.AddSingleton<SandboxSpecFactory>();
        RegisterRuntime(services, backend);
        services.AddSingleton<SandboxManager>();
        return services;
    }

    private static void RegisterRuntime(IServiceCollection services, string? backend)
    {
        switch (backend?.Trim().ToLowerInvariant())
        {
            case "kubernetes" or "k8s":
                // One typed client for the process; in-cluster ServiceAccount auth as a Pod, else kubeconfig.
                services.AddSingleton<IKubernetes>(_ => new Kubernetes(BuildKubernetesConfig()));
                services.AddSingleton<ISandboxRuntime, KubernetesSandboxRuntime>();
                break;

            case null or "" or "docker":
                services.AddSingleton<ISandboxRuntime, DockerSandboxRuntime>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown Sandbox:Backend '{backend}'. Valid values: 'docker', 'kubernetes'.");
        }
    }

    private static KubernetesClientConfiguration BuildKubernetesConfig()
    {
        // In-cluster (mounted ServiceAccount token) when running as a Pod; fall back to the local kubeconfig
        // for dev / k3d validation, where InClusterConfig throws because the SA files aren't present.
        try
        {
            return KubernetesClientConfiguration.InClusterConfig();
        }
        catch (Exception)
        {
            return KubernetesClientConfiguration.BuildConfigFromConfigFile();
        }
    }
}
