namespace Mintokei.Sandbox;

/// <summary>
/// Supplies the <see cref="SandboxSessionRequest"/> for each warm sandbox the pool provisions — the
/// seam through which the embedder plugs in enrollment (mint a one-time token), the backend URL the
/// runner dials, and (later) per-session repo/credentials. Keeps <see cref="SandboxPoolService"/> free
/// of any product or enrollment dependency (the runtime provides the loop; the embedder the policy).
/// </summary>
public interface ISandboxSessionSource
{
    Task<SandboxSessionRequest> CreateWarmRequestAsync(CancellationToken ct = default);
}
