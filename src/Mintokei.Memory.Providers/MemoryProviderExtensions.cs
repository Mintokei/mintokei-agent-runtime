namespace Mintokei.Memory.Providers;

public static class MemoryProviderExtensions
{
    /// <summary>Explicit provider routing. Failures never fall back to a different billing or authentication mode.</summary>
    public static IServiceCollection AddMemoryProviders(this IServiceCollection services)
    {
        services.AddSingleton<CodexCliProvider>();
        services.AddSingleton<CodexAppServerHome>();
        services.AddSingleton<CodexAppServerProvider>();
        services.AddSingleton<OpenAiCredentials>();
        services.AddHttpClient<OpenAiApiProvider>(c => c.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        services.AddSingleton<ILlmProvider, LlmProviderRouter>();
        return services;
    }
}
