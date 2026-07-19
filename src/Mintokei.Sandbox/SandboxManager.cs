using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mintokei.Sandbox;

/// <summary>One tracked, provisioned sandbox (a running container the control plane may dispatch to).</summary>
public sealed record SandboxLease(SandboxHandle Handle, string Profile, bool Warm);

/// <summary>
/// Owns sandbox lifecycle against an <see cref="ISandboxRuntime"/>: provisioning, one-shot recycle,
/// reaping vanished sandboxes, and topping up the warm pool. Written once against the runtime seam so
/// it is backend-agnostic (Docker now, Kubernetes later). Deliberately mechanism-only — enrollment and
/// session-completion signals are supplied by the control plane (delegates / calls), not owned here.
/// </summary>
public sealed class SandboxManager(
    ISandboxRuntime runtime,
    SandboxProfileResolver profiles,
    SandboxSpecFactory specs,
    IOptions<SandboxOptions> options,
    ILogger<SandboxManager> logger)
{
    private readonly SandboxOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, SandboxLease> _leases = new();

    /// <summary>Snapshot of currently tracked sandboxes.</summary>
    public IReadOnlyCollection<SandboxLease> Active => _leases.Values.ToArray();

    /// <summary>
    /// Claim an available warm sandbox matching <paramref name="profile"/> for a session — profile-aware
    /// selection from the pool. Atomically flips it from warm to serving (so it isn't handed out twice);
    /// the next maintenance tick provisions a replacement. Returns null when no warm sandbox of that
    /// profile is free.
    /// </summary>
    public SandboxLease? TryAcquireWarm(string profile)
    {
        foreach (var (name, lease) in _leases)
        {
            if (!lease.Warm || !string.Equals(lease.Profile, profile, StringComparison.OrdinalIgnoreCase))
                continue;

            var assigned = lease with { Warm = false };
            if (_leases.TryUpdate(name, assigned, lease))
            {
                logger.LogInformation("Sandbox {Name} acquired for a {Profile} session", name, profile);
                return assigned;
            }
        }

        return null;
    }

    /// <summary>Resolve the isolation profile, build the spec, and launch one sandbox. Names must be unique.</summary>
    public async Task<SandboxLease> ProvisionAsync(
        SandboxSessionRequest request,
        string? profileOverride = null,
        string? workspaceProfile = null,
        bool warm = false,
        CancellationToken ct = default)
    {
        var profile = profiles.Resolve(profileOverride, workspaceProfile);
        var handle = await runtime.ProvisionAsync(specs.Build(profile, request), ct);
        var lease = new SandboxLease(handle, profile.Name, warm);
        _leases[request.Name] = lease;
        logger.LogInformation("Sandbox {Name} provisioned (profile={Profile}, warm={Warm})",
            request.Name, profile.Name, warm);
        return lease;
    }

    /// <summary>Current runtime status of a provisioned sandbox (delegates to the backend). Lets a caller
    /// waiting for a runner to enroll notice the container exiting early instead of burning the full timeout.</summary>
    public Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct = default)
        => runtime.GetStatusAsync(handle, ct);

    /// <summary>Best-effort tail of a sandbox's logs, or empty when the backend can't provide them (test
    /// doubles, or a backend that doesn't implement <see cref="ISandboxLogSource"/>). Never throws — used to
    /// explain WHY a sandbox that never enrolled failed (usually a clone / credentials error).</summary>
    public async Task<string> GetLogsAsync(SandboxHandle handle, int tailLines = 40, CancellationToken ct = default)
        => runtime is ISandboxLogSource source ? await source.GetLogsAsync(handle, tailLines, ct) : string.Empty;

    /// <summary>Stop + remove a sandbox and untrack it (one-shot recycle after its single session).</summary>
    public async Task RecycleAsync(string name, CancellationToken ct = default)
    {
        if (!_leases.TryRemove(name, out var lease))
            return;

        await runtime.StopAsync(lease.Handle, ct);
        logger.LogInformation("Sandbox {Name} recycled", name);
    }

    /// <summary>
    /// Untrack + remove tracked sandboxes whose container has exited or vanished. Driven by the container's
    /// own status, never by the machine's connection state — a disconnected-but-running sandbox is a
    /// transient partition that the runner reconnects and resumes, so it is left alone.
    /// </summary>
    public async Task<int> ReapAsync(CancellationToken ct = default)
    {
        var reaped = 0;
        foreach (var (name, lease) in _leases.ToArray())
        {
            var status = await runtime.GetStatusAsync(lease.Handle, ct);
            if (status.State is SandboxState.Exited or SandboxState.NotFound)
            {
                await RecycleAsync(name, ct);
                reaped++;
            }
        }

        return reaped;
    }

    /// <summary>
    /// Reconcile against the runtime after a process restart: clean up sandbox containers that have already
    /// exited (including ones this process never tracked, e.g. leftovers from a previous run), and drop any
    /// stale tracking. RUNNING containers are deliberately left untouched — they may be live sessions whose
    /// runners will reconnect and resume; the machine's connection state is never a reason to kill them.
    /// Returns the number of exited containers cleaned up.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        var reaped = 0;
        foreach (var handle in await runtime.ListManagedAsync(ct))
        {
            var status = await runtime.GetStatusAsync(handle, ct);
            if (status.State is SandboxState.Exited or SandboxState.NotFound)
            {
                await runtime.StopAsync(handle, ct); // remove the dead container shell (idempotent)
                _leases.TryRemove(handle.Name, out _);
                reaped++;
            }
        }

        return reaped;
    }

    /// <summary>
    /// Top the warm pool up to <see cref="SandboxOptions.WarmPoolSize"/>. Warm sandboxes are repo-agnostic;
    /// the control plane supplies each one's enrollment + a unique name via <paramref name="warmRequestFactory"/>.
    /// </summary>
    public async Task MaintainWarmPoolAsync(
        Func<CancellationToken, Task<SandboxSessionRequest>> warmRequestFactory,
        CancellationToken ct = default)
    {
        var warm = _leases.Values.Count(l => l.Warm);
        for (var i = warm; i < _options.WarmPoolSize; i++)
        {
            var request = await warmRequestFactory(ct);
            await ProvisionAsync(request, warm: true, ct: ct);
        }
    }
}
