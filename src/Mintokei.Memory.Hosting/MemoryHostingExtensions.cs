namespace Mintokei.Memory.Hosting;

public static class MemoryHostingExtensions
{
    /// <summary>Run the leased PostgreSQL memory queue in this host. Chat jobs are never claimed.</summary>
    public static IServiceCollection AddMemoryProcessing(this IServiceCollection services)
    {
        services.AddHostedService<MemoryProcessingWorker>();
        return services;
    }
}
