using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Mintokei.Memory.Postgres;
/// <summary>Registers the optional PostgreSQL backend. The host supplies an ILlmProvider.</summary>
public static class MemoryServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresMemory(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddDbContextFactory<MemoryDbContext>(o => o.UseNpgsql(connectionString));
        services.TryAddSingleton<IMemoryEvents, NullMemoryEvents>();
        services.TryAddSingleton<IMemoryJobObserver, NullMemoryJobObserver>();
        services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>(c => c.Timeout = TimeSpan.FromSeconds(90));
        services.AddHttpClient<Mem0MemoryStore>();
        services.AddSingleton<PostgresMemoryStore>();
        services.AddSingleton<IArtifactStore, LocalArtifactStore>();
        services.AddSingleton<TraceRecorder>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<Reasoner>();
        services.AddSingleton<HistorySearch>();
        services.AddSingleton<IHistorySearch>(s => s.GetRequiredService<HistorySearch>());
        services.AddSingleton<AgentMemoryTools>();
        services.AddSingleton<ReferenceResolutionService>();
        services.AddSingleton<IReferenceResolutionService>(s => s.GetRequiredService<ReferenceResolutionService>());
        services.AddSingleton<MemoryPipeline>();
        services.AddSingleton<MemoryJobProcessor>();
        return services;
    }
}
