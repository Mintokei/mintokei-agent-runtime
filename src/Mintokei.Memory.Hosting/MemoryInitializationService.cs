using Microsoft.EntityFrameworkCore;

namespace Mintokei.Memory.Hosting;

/// <summary>Initialize before starting the worker; persists initial configuration once.</summary>
public sealed class MemoryInitializationService(
    IDbContextFactory<MemoryDbContext> factory,
    IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var root = configuration["Memory:Root"] ?? throw new InvalidOperationException("Memory:Root must be configured");
        var initial = new MemoryConfiguration { CapturePayloads = false };
        configuration.GetSection("Memory:Configuration").Bind(initial);
        await using var db = await factory.CreateDbContextAsync(ct);
        await DatabaseSetup.InitializeAsync(db, root, initial);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
