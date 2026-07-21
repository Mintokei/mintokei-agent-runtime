using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// Registers the remote-runner sandbox path: dispatch a sandbox container to a CHOSEN worker over the
    /// control channel (<see cref="Mintokei.Sandbox.Docker.RemoteDockerSandboxRuntime"/>) plus per-session
    /// credential staging (<see cref="SandboxCredentialStager"/>) for the non-root container. Opt-in and
    /// independent of the single-backend <see cref="ISandboxRuntime"/>; requires an
    /// <see cref="Mintokei.Runner.Contracts.IRemoteCommandRunner"/> (e.g. from <c>AddRunnerHostCore</c>).
    /// </summary>
    public static IServiceCollection AddMintokeiRemoteSandbox(this IServiceCollection services)
    {
        services.AddSingleton<SandboxCredentialStager>();
        services.AddSingleton<Mintokei.Sandbox.Docker.RemoteDockerSandboxRuntime>();
        services.AddSingleton<RemoteSandboxManager>();   // one-call facade over the three above
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
                // One typed client for the process, pointed at the configured sandbox cluster (default:
                // in-cluster ServiceAccount as a Pod, else the ambient kubeconfig).
                services.AddSingleton<IKubernetes>(sp =>
                    new Kubernetes(BuildKubernetesConfig(sp.GetRequiredService<IOptions<SandboxOptions>>().Value)));
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

    /// <summary>
    /// Resolves which cluster the Kubernetes backend talks to. Precedence: an explicit API server + token,
    /// then a kubeconfig file (the usual way to target a remote/dedicated cluster), then the default —
    /// in-cluster ServiceAccount token when running as a Pod, else the ambient kubeconfig (dev / k3d).
    /// Default behaviour (all options unset) is unchanged.
    /// </summary>
    internal static KubernetesClientConfiguration BuildKubernetesConfig(SandboxOptions o)
    {
        // 1. Explicit API-server URL + token — no kubeconfig file needed.
        if (!string.IsNullOrWhiteSpace(o.KubernetesApiServerUrl))
        {
            return new KubernetesClientConfiguration
            {
                Host = o.KubernetesApiServerUrl,
                AccessToken = o.KubernetesToken,
                SkipTlsVerify = o.KubernetesSkipTlsVerify,
            };
        }

        // 2. Explicit kubeconfig file (+ optional context) — carries the cluster's server URL, CA, and creds.
        if (!string.IsNullOrWhiteSpace(o.KubernetesKubeconfig))
            return KubernetesClientConfiguration.BuildConfigFromConfigFile(o.KubernetesKubeconfig, o.KubernetesContext);

        // 3. Default: in-cluster (ServiceAccount) as a Pod; else the ambient kubeconfig (InClusterConfig
        //    throws when the SA files aren't present, e.g. dev / k3d).
        try
        {
            return KubernetesClientConfiguration.InClusterConfig();
        }
        catch (Exception)
        {
            return KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: o.KubernetesContext);
        }
    }
}
