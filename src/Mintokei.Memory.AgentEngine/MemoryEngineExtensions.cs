using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mintokei.AgentEngine;

namespace Mintokei.Memory.AgentEngine;

public static class MemoryEngineExtensions
{
    /// <summary>Reuse the host's ICommandLineRunnerFactory; never installs a competing process runner.</summary>
    public static IServiceCollection AddMemoryAgentEngine(this IServiceCollection services)
    {
        services.AddOptions<MemoryEngineOptions>();
        services.TryAddSingleton<AgentSessionFactory>();
        services.AddSingleton<MemoryCodexHome>();
        services.AddSingleton<ILlmProvider, AgentEngineLlmProvider>();
        return services;
    }
}
