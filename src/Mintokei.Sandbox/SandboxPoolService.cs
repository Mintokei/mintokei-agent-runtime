using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mintokei.Sandbox;

/// <summary>
/// Drives the per-session sandbox pool on a timer: tops the warm pool up to
/// <see cref="SandboxOptions.WarmPoolSize"/> and reaps exited sandboxes. Each warm sandbox's request
/// (enrollment token, backend URL, name) comes from the embedder-supplied <see cref="ISandboxSessionSource"/>,
/// so this loop is pure mechanism with no product or enrollment dependency.
///
/// Dormant unless <see cref="SandboxOptions.WarmPoolSize"/> &gt; 0, so an embedder that hasn't opted in
/// is unaffected. Register with <c>AddMintokeiSandboxPool()</c> alongside an <see cref="ISandboxSessionSource"/>.
/// </summary>
public sealed class SandboxPoolService(
    SandboxManager manager,
    ISandboxSessionSource sessionSource,
    IOptions<SandboxOptions> options,
    ILogger<SandboxPoolService> logger) : BackgroundService
{
    private readonly SandboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.WarmPoolSize <= 0)
        {
            logger.LogInformation("Sandbox pool disabled (WarmPoolSize=0).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PoolIntervalSeconds));
        logger.LogInformation("Sandbox pool started: warm={Warm}, profile={Profile}, interval={Interval}s",
            _options.WarmPoolSize, _options.DefaultProfile, _options.PoolIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, stoppingToken);
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sandbox pool tick failed");
            }
        }
    }

    /// <summary>One maintenance tick: reap exited sandboxes first (so top-up sees the real gap), then top
    /// the warm pool back up to target. Exposed for tests.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await manager.ReapAsync(ct);
        await manager.MaintainWarmPoolAsync(c => sessionSource.CreateWarmRequestAsync(c), ct);
    }
}
