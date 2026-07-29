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

## What you reuse vs. implement

You **implement two things** — an `ISandboxSessionSource` and a small provision → wait-online → dispatch
→ recycle orchestration. **Everything else is reused**, including the enrollment / presence / dispatch,
which live in `Mintokei.Runner.Host` (a container's runner enrolls back exactly like any remote worker):

| Concern | Reuse or implement | Type |
|---|---|---|
| Launch / stop / inspect the container | **Reuse** | `DockerSandboxRuntime` / `KubernetesSandboxRuntime` (registered by `AddMintokeiSandbox`) |
| Container lifecycle (provision / recycle / reap / pool) | **Reuse** | `SandboxManager`, `SandboxPoolService` |
| Mint the one-time enrollment token | **Reuse** | `IRunnerEnrollment.CreateEnrollmentTokenAsync` (`Mintokei.Runner.Host`) |
| Runner presence ("came Online") | **Reuse** | `IRunnerRegistry` / `IAgentControlPlane` — `RunnerConnected`, `IsRunnerConnected` |
| Dispatch the agent session into the sandbox | **Reuse** | `IAgentControlPlane.StartSessionAsync(spec, machineId)` |
| Build the request (repos, creds, backend URL) | **Implement** | `ISandboxSessionSource` |
| provision → wait-online → bind → recycle glue | **Implement** | your own small orchestration (an "assigner") |
| *When* to recycle (session done / idle) | Reuse mechanism, own the policy | `SandboxManager.ReapAsync`/`RecycleAsync` + your trigger |

The two runnable samples show both ends of that split:

- [`samples/SandboxSessionMinimal`](../../samples/SandboxSessionMinimal) — the full lifecycle with the
  reused parts faked, so it runs **anywhere** (no Docker).
- [`samples/SandboxRunnerHostMinimal`](../../samples/SandboxRunnerHostMinimal) — the same lifecycle with
  **no fakes**: real `Runner.Host` + a real container (needs Docker + the image).

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

See the two samples above for the full lifecycle end to end;
[`samples/SandboxPoolMinimal`](../../samples/SandboxPoolMinimal) adds the warm pool.

## Sharing one sandbox between sessions

A sandbox normally serves one session. It can serve several — every task in a workspace working on one tree,
say — but sharing a sandbox shares more than the tree, and the library makes you say so explicitly.

A sandbox has **one broker on one network**, and the broker cannot tell which session a connection came from.
Its egress allowlist and the credentials it injects therefore apply to *every* session inside. Letting a
session join a sandbox that was not built for its tool silently grants the sessions already in there that
tool's network reach and credentials — for the sandbox's whole life, since the allowlist is fixed when the
broker starts.

So there are two halves: **declare** what a sandbox serves, and **check** before anyone joins.

```csharp
// 1. Provisioning: declare the tools this sandbox may serve, and key the working tree by whatever OWNS it
//    (a workspace, a project — not the individual session, or nothing can be shared).
var sandbox = await provisioner.ProvisionAsync(new SandboxProvisionRequest
{
    Profile = "standard",
    Repos   = repos,
    AdmittedTools          = ["ClaudeCodeCli"],   // omit → single-session, forever
    PersistentWorkspaceKey = workspaceId,

    // One container, one cgroup: N sessions share a single memory limit, and an OOM takes ALL of them.
    // Size the ceiling for what the sandbox may host. The reserve stays null — it decides how many
    // sandboxes fit a node, and raising it costs density whether or not the headroom is used.
    LimitsOverride = new SandboxResources(
        MemoryLimitBytes: 8L * 1024 * 1024 * 1024, CpuLimit: 2, PidsLimit: 512),
});

// 2. Joining: check BEFORE dispatching a second session into it. Reads the declaration off the sandbox
//    itself, so a stale local record cannot let a session in.
await provisioner.EnsureCanAttachAsync(existingHandle, "ClaudeCodeCli", ct);   // throws SandboxAdmissionException
```

`EnsureCanAttachAsync` refuses in three cases, all deliberate:

| case | why |
|---|---|
| the tool is not declared | it would widen egress + credentials for the sessions already inside |
| the sandbox carries **no** declaration | it is single-session — whoever is in it never agreed to share |
| the backend cannot report a declaration | nothing to check against; sharing is unavailable, not assumed safe |

Note the second one. Everywhere else an empty `AdmittedTools` means *unconstrained* (a sandbox serving the one
session it was built for, with nothing to admit). At the attach gate it means the opposite — **do not join**.
That inversion is the easiest thing here to get backwards, and getting it backwards opens every pre-existing
sandbox to joiners.

Reclaiming a shared workspace store is the mirror image: it belongs to *several* sessions, so it may only be
removed once **all** of them are done. Deciding from one session deletes a working tree the others are still
using.

## Egress postures

A profile picks one of three, via `Sandbox:Profiles:<name>:Egress`:

| Posture | Network | Credentials |
|---|---|---|
| `open` (default) | unrestricted | seeded into the container under `/seed` |
| `proxy` | routed through an allowlisting CONNECT proxy — **advisory**, honoured only by clients that obey `HTTP(S)_PROXY` | still seeded |
| `broker` | **deny-by-default**: a per-session network whose only reachable peer is the session's broker (Docker: an `--internal` network) | **none seeded** — the broker injects short-lived, scoped credentials on demand |

`broker` is the hardened posture, and the one worth understanding before you choose it: the network is the
enforcement, not the proxy env vars, so a process that ignores them still has no route out.

### Broker egress requires an `https` backend the sandbox trusts

This is a **deployment constraint, not a config detail** — decide it before you adopt the posture.

Because the sandbox's only route out is the broker's CONNECT proxy, and .NET only CONNECT-tunnels TLS, a
plaintext `http://` backend URL would not traverse the proxy at all — and on a deny-by-default network it
simply never connects. So:

- **`BackendUrl` / `GrpcBackendUrl` must be `https://`** — enforced at provision time, fail-closed with an
  explicit message.
- **Their host must also be in the profile's `EgressAllowlist`** — *not* enforced, because the allowlist is
  opaque host matching and the URL may legitimately be reached via a name the list spells differently. Only
  its non-emptiness is checked. Omit the backend host and provisioning succeeds; the broker then refuses the
  CONNECT and the runner never enrols, which reads as a startup failure rather than a config mistake.
- **The sandbox must trust that certificate.** It validates normally — there is no skip-verify switch, and no
  per-session way to inject a CA. Behind an internal CA (most on-prem deployments) you must bake it into your
  own sandbox image: `COPY ca.crt /usr/local/share/ca-certificates/ && update-ca-certificates`.
- **`AddHostGateway` is rejected** in this posture — host reachability defeats containment, so the
  `host.docker.internal` shortcut every other setup relies on is unavailable by design.

The practical consequence: broker egress cannot be pointed at a plain `http://localhost` backend the way the
open posture can. [`samples/BrokerSandboxMinimal`](../../samples/BrokerSandboxMinimal) documents the full
local loop, including the TLS wiring, and marks which parts are covered by tests versus environment-specific.

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
