using Microsoft.Extensions.Configuration;
using Mintokei.Runner;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.AgentTools.Claude;
using Mintokei.AgentEngine.AgentTools.Codex;
using Mintokei.AgentEngine.AgentTools.Copilot;
using Mintokei.AgentEngine.AgentTools.Gemini;
using Mintokei.AgentEngine.AgentTools.OpenCode;
using Mintokei.AgentEngine.CommandRunner;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers a Mintokei runner into any generic host — the whole client (enrollment, token refresh,
/// the SignalR + gRPC transports, the local outbox, file watching, the in-process file server, and the
/// tunnel). A host adds this, provides <see cref="RunnerOptions"/> (BackendUrl / EnrollmentToken /
/// DataDir / …), and runs; the runner enrolls on first boot and connects. This is what lets a runner be
/// embedded in a process, not only launched via the <c>mintokei-runner</c> executable.
/// </summary>
public static class MintokeiRunnerServiceCollectionExtensions
{
    /// <summary>Registers the runner, binding <see cref="RunnerOptions"/> from the <c>Runner</c>
    /// configuration section (the <c>mintokei-runner</c> executable's path).</summary>
    public static IServiceCollection AddMintokeiRunner(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RunnerOptions>(configuration.GetSection("Runner"));
        return services.AddMintokeiRunnerCore();
    }

    /// <summary>Registers the runner, configuring <see cref="RunnerOptions"/> in code — for embedders
    /// that set the backend URL / enrollment token / data dir directly rather than from configuration.</summary>
    public static IServiceCollection AddMintokeiRunner(this IServiceCollection services, Action<RunnerOptions> configure)
    {
        services.Configure(configure);
        return services.AddMintokeiRunnerCore();
    }

    private static IServiceCollection AddMintokeiRunnerCore(this IServiceCollection services)
    {
        services.AddSingleton<ICommandLineRunner, CommandLineRunner>();

        // Model-discovery providers — invoked during handshake on the runner's host. Keyed by
        // AgentToolKey so the handshake loop looks up the right provider for each probe spec.
        services.AddKeyedSingleton<IModelDiscoveryProvider, ClaudeCodeModelDiscoveryProvider>(AgentToolKey.ClaudeCodeCli);
        services.AddKeyedSingleton<IModelDiscoveryProvider, CodexModelDiscoveryProvider>(AgentToolKey.CodexCli);
        services.AddKeyedSingleton<IModelDiscoveryProvider, CopilotModelDiscoveryProvider>(AgentToolKey.GithubCopilotCli);
        services.AddKeyedSingleton<IModelDiscoveryProvider, OpenCodeModelDiscoveryProvider>(AgentToolKey.OpenCodeCli);
        services.AddKeyedSingleton<IModelDiscoveryProvider, GeminiModelDiscoveryProvider>(AgentToolKey.GeminiCli);

        services.AddSingleton<LocalOutbox>();
        services.AddSingleton<EnrollmentService>();
        services.AddSingleton<TokenRefreshService>();
        services.AddSingleton<FileWatcherService>();
        services.AddSingleton<GrpcTaskStreamManager>();
        services.AddSingleton<GrpcWatcherStreamManager>();
        services.AddSingleton<GrpcQueryStreamManager>();
        services.AddSingleton<GrpcBulkStreamManager>();

        // File server must be a singleton so RunnerHostedService can read the bound Port at handshake
        // time, and hosted first so its StartAsync runs before the SignalR-driven RunnerHostedService.
        services.AddSingleton<RunnerFileServer>();
        services.AddHostedService(sp => sp.GetRequiredService<RunnerFileServer>());

        services.AddHostedService<RunnerHostedService>();

        // gRPC transport — a singleton (RunnerHostedService wires its QueryDispatcher delegate) plus a
        // hosted-service indirection so AddHostedService doesn't spin up a second instance.
        services.AddSingleton<GrpcRunnerHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<GrpcRunnerHostedService>());

        services.AddHostedService<TunnelClient>();

        return services;
    }
}
