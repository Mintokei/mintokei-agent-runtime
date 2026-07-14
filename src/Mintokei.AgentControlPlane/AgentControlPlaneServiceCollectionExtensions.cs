using Mintokei.AgentControlPlane;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI wiring for the agent-session control plane (Mintokei.AgentControlPlane).
/// </summary>
public static class AgentControlPlaneServiceCollectionExtensions
{
    /// <summary>
    /// Registers the DB-free agent-session control plane: <c>DefaultAgentControlPlane</c> (the one singleton that
    /// holds every live session, runner presence, and capacity), its session launcher, and the
    /// connection tracker. All public seams resolve to that same singleton, so inject whichever fits:
    /// <list type="bullet">
    ///   <item><see cref="IAgentControlPlane"/> — the broad front door: session lifecycle
    ///   (start/stop/register/list + events) <em>and</em> runner presence (it inherits
    ///   <see cref="IRunnerRegistry"/>). Inject this to do everything from one service.</item>
    ///   <item><see cref="IRunnerRegistry"/> — presence only, for a transport that just reports
    ///   connect/disconnect (interface segregation).</item>
    ///   <item><see cref="ICapacityLedger"/> — the admission/capacity surface (advanced).</item>
    /// </list>
    ///
    /// The host must additionally register the agent backends the launcher composes: one
    /// <see cref="Mintokei.AgentEngine.IAgentBackend"/> per tool (the launcher takes them as an
    /// <c>IEnumerable</c>), plus an <see cref="Mintokei.AgentEngine.CommandRunner.ICommandLineRunnerFactory"/>
    /// (local, or Mintokei.Runner.Host's for remote runners) and an <c>ILoggerFactory</c>.
    /// </summary>
    public static IServiceCollection AddAgentControlPlane(this IServiceCollection services)
    {
        services.AddSingleton<RunnerConnectionTracker>();
        services.AddSingleton<AgentSessionLauncher>();
        services.AddSingleton<DefaultAgentControlPlane>();

        // The three public contracts onto the one DefaultAgentControlPlane singleton.
        services.AddSingleton<IAgentControlPlane>(sp => sp.GetRequiredService<DefaultAgentControlPlane>());
        services.AddSingleton<IRunnerRegistry>(sp => sp.GetRequiredService<DefaultAgentControlPlane>());
        services.AddSingleton<ICapacityLedger>(sp => sp.GetRequiredService<DefaultAgentControlPlane>());

        return services;
    }
}
