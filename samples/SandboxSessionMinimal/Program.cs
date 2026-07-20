using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.Sandbox;

// Standalone demo of running ONE agent session in a sandbox with Mintokei.Sandbox — the on-demand path:
//   build a session request → provision a per-session container → its in-container runner enrolls back
//   → dispatch a session to that machine → one-shot recycle.
// No product, no backend, no Docker required: a fake ISandboxRuntime stands in for `docker run`, and a
// fake backend stands in for Mintokei.Runner.Host. Those two fakes are the ONLY things you swap in
// production (keep the default DockerSandboxRuntime; provide a token-minting ISandboxSessionSource, and
// dispatch the session through Runner.Host / IAgentControlPlane once the runner comes Online).

// Stand-in for YOUR backend (Mintokei.Runner.Host): mints enrollment tokens, marks a machine Online when
// its runner dials back, and dispatches agent sessions to Online machines.
var backend = new FakeBackend();

var services = new ServiceCollection();
services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>)); // no-op logging for the demo
services.AddMintokeiSandbox(o =>
{
    o.Backend = "docker";                                  // the real default; we override the runtime below
    o.Image = "ghcr.io/mintokei/mintokei-sandbox:latest";
    o.DefaultProfile = "standard";
    o.AllowedProfiles = ["standard"];
    o.Profiles["standard"] = new SandboxProfileConfig { Runtime = "runc", MemoryMb = 4096, Cpus = 2 };
});

// Swap the real Docker runtime for a fake that "boots the container": it reads the runner flags the
// SandboxSpecFactory placed on the container args and hands them to the fake backend, exactly as the
// in-container Mintokei.Runner would when it enrolls. Last ISandboxRuntime registration wins.
services.AddSingleton<ISandboxRuntime>(new FakeContainerRuntime(backend));
services.AddSingleton<ISandboxSessionSource>(new DemoSessionSource(backend));

await using var provider = services.BuildServiceProvider();
var manager = provider.GetRequiredService<SandboxManager>();
var source = provider.GetRequiredService<ISandboxSessionSource>();

// 1. Build the session request — the ONE seam you own: an enrollment token + the backend URL the
//    container dials, plus optional repos to clone and agent/git credentials to seed.
var request = await source.CreateWarmRequestAsync();
Console.WriteLine($"== provision sandbox '{request.Name}' (token={request.EnrollmentToken}, backend={request.BackendUrl}) ==");
foreach (var repo in request.Repos)
    Console.WriteLine($"   will clone {repo.Url} @ {repo.Branch ?? "default"} → {SandboxSpecFactory.RepoRoot}/<name>");

// 2. Provision the container. In production this shells `docker run`; the container entrypoint clones the
//    repos and execs Mintokei.Runner, which enrolls back into your backend over gRPC.
var lease = await manager.ProvisionAsync(request);
Console.WriteLine($"   container up: {lease.Handle.Name} (backend={lease.Handle.Backend}, profile={lease.Profile})");

// 3. Wait for the in-container runner to come Online. GetStatusAsync lets you bail early if the container
//    exits BEFORE enrolling — almost always a repo-clone / git-credentials error in the entrypoint.
await WaitUntilOnlineAsync(backend, manager, lease);

// 4. Dispatch the agent session to that machine — the SAME IAgentSession API as any remote runner. In
//    production: IAgentControlPlane.StartSessionAsync(machineId, spec) over Mintokei.Runner.Host.
Console.WriteLine("== session ==");
await backend.RunSessionAsync(request.Name, "Summarise this repository.");

// 5. One-shot recycle: the sandbox served its single session — stop + remove the container and untrack it.
await manager.RecycleAsync(request.Name);
Console.WriteLine($"== recycled '{request.Name}' — done ==");
return 0;

static async Task WaitUntilOnlineAsync(FakeBackend backend, SandboxManager manager, SandboxLease lease)
{
    for (var i = 0; i < 50 && !backend.IsOnline(lease.Handle.Name); i++)
    {
        var status = await manager.GetStatusAsync(lease.Handle);
        if (status.State is SandboxState.Exited or SandboxState.NotFound)
            throw new InvalidOperationException(
                $"sandbox '{lease.Handle.Name}' exited before enrolling — inspect its logs " +
                "(SandboxManager.GetLogsAsync); usually a repo-clone / git-credentials error.");
        await Task.Delay(20);
    }

    Console.WriteLine($"   runner '{lease.Handle.Name}' enrolled and is Online");
}

// --- demo fakes (swap for DockerSandboxRuntime + a token-minting ISandboxSessionSource + Runner.Host) ---

// Stands in for Mintokei.Runner.Host: enrollment + presence + session dispatch.
sealed class FakeBackend
{
    private readonly HashSet<string> _online = [];

    // Runner.Host mints a one-time enrollment token (bound to a pre-created machine id) against its DB.
    public string MintEnrollmentToken(string machineName) => $"enroll-{machineName}";

    // Called when a container's runner dials back with its token — Runner.Host flips the machine Online.
    public void RunnerEnrolled(string backendUrl, string token, string machineName)
    {
        Console.WriteLine($"   [backend] runner dialed {backendUrl} with '{token}' → machine '{machineName}' Online");
        _online.Add(machineName);
    }

    public bool IsOnline(string machineName) => _online.Contains(machineName);

    // Dispatch a session to an Online machine (in prod: IAgentControlPlane.StartSessionAsync over gRPC).
    public async Task RunSessionAsync(string machineName, string prompt)
    {
        if (!IsOnline(machineName))
            throw new InvalidOperationException($"machine '{machineName}' is not Online");
        Console.WriteLine($"   [backend] dispatch to '{machineName}': \"{prompt}\"");
        await Task.Delay(50);
        Console.WriteLine("   [backend] agent streamed its reply (normalized AgentMessages) and finished the turn");
    }
}

// Stands in for DockerSandboxRuntime: instead of `docker run`, simulate booting the container + its runner.
sealed class FakeContainerRuntime(FakeBackend backend) : ISandboxRuntime
{
    private readonly HashSet<string> _exited = [];

    public string Backend => "fake-docker";

    public Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        Console.WriteLine($"   [runtime] docker run {spec.Image} " +
            $"(--runtime {spec.RuntimeClass}, mem={spec.Limits.MemoryBytes >> 20}MiB, cpus={spec.Limits.Cpus})");

        // The real container execs `mintokei-runner <spec.Args>`; the args are --backend/--token/--name.
        var flags = ParseFlags(spec.Args);
        backend.RunnerEnrolled(flags["--backend"], flags["--token"], flags["--name"]);

        return Task.FromResult(new SandboxHandle($"ctr-{spec.Name}", spec.Name, Backend));
    }

    public Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct = default)
        => Task.FromResult(new SandboxStatus(_exited.Contains(handle.Name) ? SandboxState.Exited : SandboxState.Running));

    public Task StopAsync(SandboxHandle handle, CancellationToken ct = default)
    {
        Console.WriteLine($"   [runtime] docker rm -f {handle.Name}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SandboxHandle>> ListManagedAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SandboxHandle>>([]);

    private static Dictionary<string, string> ParseFlags(IReadOnlyList<string> args)
    {
        var flags = new Dictionary<string, string>();
        for (var i = 0; i + 1 < args.Count; i += 2)
            flags[args[i]] = args[i + 1];
        return flags;
    }
}

// Stands in for your product's session source: mint enrollment + describe the repos/creds for one session.
sealed class DemoSessionSource(FakeBackend backend) : ISandboxSessionSource
{
    public Task<SandboxSessionRequest> CreateWarmRequestAsync(CancellationToken ct = default)
    {
        const string name = "sandbox-001";
        return Task.FromResult(new SandboxSessionRequest
        {
            // Must be reachable from INSIDE the container — a public ingress carrying HTTP/2 (for the gRPC
            // control stream), never an in-cluster DNS name a container can't resolve.
            BackendUrl = "https://your-ingress.example",
            EnrollmentToken = backend.MintEnrollmentToken(name),
            Name = name,
            Repos = [new SandboxRepoSpec("https://github.com/me/repo.git", Branch: "main")],
            ClaudeConfigHostDir = "/home/runner/.claude",     // seeded RO at /seed → writable HOME in-container
            GitCredentialsHostDir = "/home/runner/git-creds", // for cloning a private repo over the network
        });
    }
}
