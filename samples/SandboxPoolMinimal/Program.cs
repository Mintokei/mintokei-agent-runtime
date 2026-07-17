using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Sandbox;

// Standalone demo of the Mintokei.Sandbox pool layer — no product, no backend, no Docker required.
// Wires AddMintokeiSandbox with in-code options, swaps the real DockerSandboxRuntime for a logging
// fake so it runs anywhere, supplies a demo ISandboxSessionSource, and drives the pool tick
// (SandboxPoolService.RunOnceAsync): reap → warm-pool top-up, plus a one-shot recycle. In production
// you keep the default DockerSandboxRuntime and provide an ISandboxSessionSource that mints real
// enrollment tokens against your backend.

var services = new ServiceCollection();
services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>)); // no-op logging for the demo
services.AddMintokeiSandbox(o =>
{
    o.Image = "demo/sandbox:latest";
    o.WarmPoolSize = 3;
    o.DefaultProfile = "standard";
    o.AllowedProfiles = ["standard"];
    o.Profiles["standard"] = new SandboxProfileConfig { Runtime = "runc" };
});

// Swap the real Docker runtime for a logging fake (last ISandboxRuntime registration wins).
var runtime = new LoggingSandboxRuntime();
services.AddSingleton<ISandboxRuntime>(runtime);
services.AddSingleton<ISandboxSessionSource, DemoSessionSource>();

await using var provider = services.BuildServiceProvider();
var manager = provider.GetRequiredService<SandboxManager>();
var source = provider.GetRequiredService<ISandboxSessionSource>();
var options = provider.GetRequiredService<IOptions<SandboxOptions>>();
var pool = new SandboxPoolService(manager, source, options, NullLogger<SandboxPoolService>.Instance);

Console.WriteLine("== tick 1: top the warm pool up to 3 ==");
await pool.RunOnceAsync();
Report(manager);

Console.WriteLine("\n== one-shot recycle: a session finished on the first sandbox ==");
await manager.RecycleAsync(manager.Active.First().Handle.Name);
Report(manager);

Console.WriteLine("\n== tick 2: refill to 3 ==");
await pool.RunOnceAsync();
Report(manager);

Console.WriteLine("\n== a sandbox's container exits; tick 3 reaps it and refills in one tick ==");
runtime.MarkExited(manager.Active.First().Handle.Name);
await pool.RunOnceAsync();
Report(manager);

Console.WriteLine("\nDone.");
return 0;

static void Report(SandboxManager m) =>
    Console.WriteLine($"   active sandboxes: {m.Active.Count} [{string.Join(", ", m.Active.Select(a => a.Handle.Name))}]");

// --- demo fakes (swap for DockerSandboxRuntime + a token-minting ISandboxSessionSource in production) ---

sealed class LoggingSandboxRuntime : ISandboxRuntime
{
    private readonly HashSet<string> _exited = [];

    public string Backend => "demo";

    public Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        Console.WriteLine($"   [runtime] provision {spec.Name}  (image={spec.Image}, runtime={spec.RuntimeClass})");
        return Task.FromResult(new SandboxHandle($"demo-{spec.Name}", spec.Name, Backend));
    }

    public Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct = default)
        => Task.FromResult(new SandboxStatus(_exited.Contains(handle.Name) ? SandboxState.Exited : SandboxState.Running));

    public Task StopAsync(SandboxHandle handle, CancellationToken ct = default)
    {
        Console.WriteLine($"   [runtime] stop {handle.Name}");
        return Task.CompletedTask;
    }

    public void MarkExited(string name) => _exited.Add(name);
}

sealed class DemoSessionSource : ISandboxSessionSource
{
    private int _n;

    public Task<SandboxSessionRequest> CreateWarmRequestAsync(CancellationToken ct = default)
    {
        var n = ++_n;
        return Task.FromResult(new SandboxSessionRequest
        {
            BackendUrl = "https://backend.example",
            EnrollmentToken = $"demo-token-{n}",
            Name = $"sandbox-{n:D3}",
        });
    }
}
