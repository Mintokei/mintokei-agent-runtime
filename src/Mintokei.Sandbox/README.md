# Mintokei.Sandbox

Run **each agent session in its own throwaway, resource-capped container** — per-session OS isolation
for the Mintokei runner. A sandbox is a container that boots the `Mintokei.Runner` binary, enrolls back
into your backend exactly like a remote worker, serves one session, and is recycled. The pool and
lifecycle logic is written once against a backend seam, so the same code runs on **Docker** or
**Kubernetes**.

> **Status:** experimental (`0.1.x`). Unlike the rest of the runtime it is **not published to NuGet
> yet** — reference it in-tree (a project reference, or the `external/mintokei-agent-runtime` submodule).
> Public APIs may change.

## How it works

A sandbox is just a normal remote runner that happens to live in a per-session container:

```text
  Backend (yours: Mintokei.Runner.Host)           Sandbox container (this library launches it)
  ─────────────────────────────────────           ─────────────────────────────────────────────
  ISandboxSessionSource: mint enroll token ──────► docker run <image> --backend <url> --token <t>
  SandboxManager.ProvisionAsync ─── docker run ──► entrypoint: clone repos, seed creds, exec runner
  runner presence (gRPC control) ◄─── enroll ───── Mintokei.Runner dials your PUBLIC backend URL
  dispatch a session to that machine id  ────────► the agent CLI runs INSIDE the container
  SandboxManager.RecycleAsync ─── docker rm ─────► one-shot: container removed after its session
```

The library owns only the **container lifecycle** — provision / status / recycle / reap / warm pool.
Enrollment and session dispatch stay in *your* backend, which keeps `Mintokei.Sandbox` free of any
product or protocol coupling.

## Core pieces

- **`ISandboxRuntime`** — launch / inspect / stop one container. Implementations: `DockerSandboxRuntime`
  (shells the `docker` CLI) and `KubernetesSandboxRuntime` (typed client, in-cluster ServiceAccount).
  Selected by `Sandbox:Backend` (`docker` | `kubernetes`); one backend per host process.
- **`SandboxManager`** — lifecycle over that seam: `ProvisionAsync`, `RecycleAsync` (one-shot),
  `ReapAsync` (drop exited), `ReconcileAsync` (recover after a process restart), warm-pool top-up, and
  `TryAcquireWarm`.
- **`SandboxSpecFactory` / `SandboxSpec`** — turn a `SandboxSessionRequest` (token, backend URL, repos,
  creds) plus a resolved profile into the concrete container spec: image, `--runtime` class, cgroup
  caps, mounts, env, and the runner CLI flags.
- **`SandboxProfileResolver` / `SandboxProfileConfig`** — named isolation tiers: OCI runtime
  (`runc` | `runsc` gVisor | `kata-fc` Firecracker) + mem/cpu/pids caps + egress posture.
- **`ISandboxSessionSource`** — the one seam you implement: mint an enrollment token against your
  backend and describe the session (repos, credentials).
- **`SandboxPoolService` / `AddMintokeiSandboxPool`** — optional hosted service that keeps N warm,
  repo-agnostic sandboxes online on a timer.

## Minimal usage

```csharp
services.AddMintokeiSandbox(o =>
{
    o.Backend = "docker";                                  // or "kubernetes"
    o.Image   = "ghcr.io/mintokei/mintokei-sandbox:latest";
    o.DefaultProfile = "standard";
    o.AllowedProfiles = ["standard"];
    o.Profiles["standard"] = new SandboxProfileConfig
    {
        Runtime = "runc", MemoryMb = 4096, Cpus = 2, PidsLimit = 512, Egress = "open",
    };
});

// The one seam you implement: mint enrollment against YOUR backend + describe the session.
sealed class MySessionSource(IMyEnroller enroller) : ISandboxSessionSource
{
    public async Task<SandboxSessionRequest> CreateWarmRequestAsync(CancellationToken ct)
    {
        var (token, name) = await enroller.MintAsync(ct);   // Runner.Host mints a one-time token
        return new SandboxSessionRequest
        {
            BackendUrl      = "https://your-ingress",        // reachable from INSIDE the container
            EnrollmentToken = token,
            Name            = name,
            Repos = [new SandboxRepoSpec("https://github.com/me/repo.git", Branch: "main")],
        };
    }
}

// Provision on demand, wait for the runner to enroll, dispatch a session, recycle:
var lease = await manager.ProvisionAsync(await source.CreateWarmRequestAsync(ct), ct: ct);
// … poll Runner.Host until machine `lease.Handle.Name` is Online, then dispatch as any remote runner …
await manager.RecycleAsync(lease.Handle.Name, ct);
```

See [`samples/SandboxSessionMinimal`](../../samples/SandboxSessionMinimal) for the full lifecycle end to
end (runnable, no Docker), and [`samples/SandboxPoolMinimal`](../../samples/SandboxPoolMinimal) for the
warm pool.

## Configuration (`Sandbox` section)

| Key | Default | Notes |
|---|---|---|
| `Backend` | `docker` | `docker` \| `kubernetes` (alias `k8s`). One backend per process. |
| `Image` | `mintokei/sandbox:latest` | The sandbox image (built from [`Dockerfile.sandbox`](../../Dockerfile.sandbox)). |
| `DefaultProfile` / `AllowedProfiles` | `standard` | Profile precedence: session → workspace → default, clamped to the allow-list. |
| `Profiles` | — | Named `SandboxProfileConfig` tiers (runtime + mem/cpu/pids + egress). |
| `WarmPoolSize` / `PoolIntervalSeconds` | `0` / `15` | Warm-pool size and maintenance cadence (`0` = no pool). |
| `Kubernetes*` | — | Namespace, image-pull policy, and which cluster to target (kubeconfig / API-server+token / in-cluster). Ignored by the Docker backend. |

Two operational must-knows for the container:

- **`BackendUrl` must be reachable from inside the container** — a public ingress carrying HTTP/2 (the
  runner's gRPC control stream is what marks it Online), never an in-cluster DNS name a container can't
  resolve.
- **Runner config is passed as CLI flags** (`--backend` / `--token` / `--name`), *not* `Runner__*` env
  vars — the runner re-adds `appsettings.json` after the env source, which would otherwise shadow them.

## The sandbox image

`Dockerfile.sandbox` (repo root) wraps the self-contained `Mintokei.Runner` binary + the agent CLIs
(`claude`, `codex`, …) + git. Its entrypoint clones the requested repos, seeds credentials from the RO
`/seed` mounts, then execs the runner — which **dials out only** (no ports exposed). Build it and push
to a registry each host-capable runner can pull, then point `Sandbox:Image` at it.

## Design

The full rationale — isolation profiles, the enroll-back model, the warm pool, and the phased
hardening plan — is in
[`docs/sandboxed-runner-isolation-plan.md`](../../docs/sandboxed-runner-isolation-plan.md).

Part of the **Mintokei Agent Runtime**.

## License

MIT
