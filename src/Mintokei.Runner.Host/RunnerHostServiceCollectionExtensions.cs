using Microsoft.Extensions.DependencyInjection.Extensions;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.Runner.Host;
using Mintokei.Runner.Host.CommandRunner;
using Mintokei.Runner.Host.RemoteExecution;
using Mintokei.Runner.Host.RemoteExecution.Grpc;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI wiring for the runner data-plane transport that lives in Mintokei.Runner.Host.
/// </summary>
public static class RunnerHostServiceCollectionExtensions
{
    /// <summary>
    /// Registers the runner data-plane transport core: the durable outbox pump + periodic cleanup,
    /// the five gRPC stream registries, the command-runner bridge over the outbox, the remote-process
    /// store/dispatcher, and the in-memory connection / pending-query / file-server-port stores.
    ///
    /// The CLI-probe list is host configuration — set <c>configure(o =&gt; o.CliProbesProvider = ...)</c>
    /// (it defaults to no probes). The optional reaction seam
    /// <see cref="Mintokei.Runner.Host.IRunnerHost"/> defaults to a no-op
    /// (<see cref="NullRunnerHost"/>, registered here with <c>TryAdd</c>); a host that wants to react
    /// to transport events registers its own before/after this call and it wins. Beyond that, the host
    /// provides the remaining seams and, to accept connections, the gRPC endpoint:
    /// <list type="bullet">
    ///   <item><see cref="IRemoteProcessRecovery"/> — recover a process handle for a correlation whose
    ///   in-memory handle was lost (walks the product's task table).</item>
    ///   <item><c>RunnerHostDbContext</c> — the overlay EF context over the host's SQLite file
    ///   (provider + connection string + interceptors are host configuration).</item>
    ///   <item>To serve gRPC: <c>AddGrpc()</c> + <c>MapGrpcService&lt;RunnerLinkService&gt;()</c> on
    ///   a dedicated HTTP/2 listener.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddRunnerHostCore(
        this IServiceCollection services, Action<RunnerHostOptions>? configure = null)
    {
        // Transport config (the CLI-probe provider). Always register the options so IOptions resolves
        // with defaults even when the host passes no configure delegate.
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<RunnerHostOptions>();

        // The reaction seam defaults to a no-op; a host's own IRunnerHost registration takes precedence.
        services.TryAddSingleton<IRunnerHost, NullRunnerHost>();

        // Registries of open gRPC streams — one per bidi stream. Singletons so the outbox pump and the
        // RunnerLink handlers share a single open-stream view. If gRPC is disabled no streams ever
        // register and the pump's TrySendAsync simply returns false.
        services.AddSingleton<GrpcControlChannelRegistry>();
        services.AddSingleton<GrpcTaskChannelRegistry>();
        services.AddSingleton<GrpcWatcherChannelRegistry>();
        services.AddSingleton<GrpcQueryChannelRegistry>();
        services.AddSingleton<GrpcBulkChannelRegistry>();

        // Durable outbox: the pump (also a hosted service) + the periodic acked/expired cleanup.
        services.AddSingleton<OutboxProcessorService>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxProcessorService>());
        services.AddHostedService<OutboxCleanupService>();

        // Command-runner bridge over the outbox + remote-process plumbing.
        services.AddSingleton<IRunnerMessageEnqueuer, RunnerMessageEnqueuer>();
        services.AddSingleton<ICommandLineRunnerFactory, CommandLineRunnerFactory>();
        services.AddSingleton<RemoteProcessStore>();
        services.AddSingleton<RemoteProcessOutputDispatcher>();

        // In-memory transport bookkeeping. (RunnerConnectionTracker moved up to the control plane —
        // it's a presence primitive, registered by AddAgentControlPlane().)
        services.AddSingleton<PendingQueryStore>();
        services.AddSingleton<RunnerFileServerPortStore>();

        return services;
    }
}
