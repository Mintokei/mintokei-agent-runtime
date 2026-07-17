# Sandboxed Runner Isolation Plan

Run each agent session inside its own container by wrapping `Mintokei.Runner.Client`, so that
sessions are isolated from the host and from each other — with a per-session-selectable
isolation strength — while reusing the existing dial-out runner, capacity ledger, and outbox
machinery unchanged.

Status: **design / decision recorded, not started.** Companion to
`runner-host-extraction-plan.md`.

---

## 1. Goal & non-goals

**Goal.** Give every agent session an OS-level sandbox (namespaces + cgroups, escalating to
gVisor/microVM) instead of running the coding-agent CLI as a bare subprocess on a shared
worker. Isolation strength is a **profile** chosen per session, defaulting from config.

**Non-goals.**
- Not replacing the CLIs with agentOS's in-process V8 VM. Coding agents run native toolchains
  (`dotnet`, `cargo`, `pnpm`, `git`, dev servers); they need a full OS, which an in-process
  isolate cannot provide. This plan is OS-level sandboxing, not a runtime swap.
- No change to the dispatch/outbox hot path. Machine selection, `RunnerLink` streaming, and the
  durable outbox stay as-is; the container is a new *packaging* of the existing runner.
- Not building gVisor/Firecracker support up front. See §7 (decision) — we ship one runtime and
  keep the seam open.

---

## 2. How agents run today (baseline)

The runner is a thin process executor. The coding-agent CLI (`claude`, `codex`, `copilot`,
`opencode`) is launched by `CommandLineRunner` (`UseShellExecute=false`) with a host-supplied
`WorkingDirectory`, inheriting the runner process's `HOME`/`PATH`
(`src/Mintokei.AgentEngine/CommandRunner/CommandLineRunner.cs:80-116`). There is **no per-session
OS isolation**:

- One worker = one **machine**, multiplexing many sessions up to `RunnerMachine.MaxInstances`
  (default 5) — `src/Mintokei.Runner.Host/Domain/Machines/RunnerMachine.cs:20`.
- All sessions on a worker share one OS user, one `~/.claude`/`~/.codex`, one `PATH`, and one
  `runner-outbox.db` — `scripts/install-runner.sh:70-73`.
- Worktrees are created on the worker via a `RunCommand` FS-RPC running
  `git worktree add <parent>/mkwt/<branch>` — `src/Mintokei.Api/Infrastructure/IO/RemoteGitOperations.cs:57-73`.
- Filesystem RPCs are **not rooted** at a workspace; traversal is guarded only by `..` checks
  (`RunnerFileServer` notes path validation is the API's responsibility).

Isolation today is worktree/directory separation on a shared kernel — not a sandbox.

## 3. Why this maps cleanly onto the runner

Three existing properties make container-per-session a small change, not a rewrite:

1. **The runner dials out.** gRPC control stream + WS tunnel are outbound from the runner to the
   API (`src/Mintokei.Runner.Client/GrpcRunnerHostedService.cs:125`,
   `src/Mintokei.Runner.Client/TunnelClient.cs:94`). A container needs **zero inbound ports**.
2. **A worker already == a machine** with host-side capacity accounting (`ICapacityLedger` +
   `RunnerMachine.MaxInstances`). "One container = one single-slot machine" drops into the
   existing ledger with no new admission logic.
3. **Isolation seams already exist:** per-runner `--data-dir` (creds + outbox), `MachineId`/
   `Secret` identity (`src/Mintokei.Runner.Client/EnrollmentService.cs`), and the CLI inherits
   the runner's `HOME`/`PATH`. Give each container its own of these and sessions are isolated.

## 4. Target design

```
  session  ==  1 container  ==  1 ephemeral runner  ==  1 machine (MaxInstances=1)
```

The control-plane hot path (`AgentSessionLauncher.StartSessionAsync`,
`src/Mintokei.AgentControlPlane/AgentSessionLauncher.cs:44-69`) is unchanged: it still picks an
available connected machine and dispatches `StartProcess`. New logic lives in one new component
plus small runner/host tweaks.

### 4.1 Container-runtime abstraction (orchestrator-agnostic)

One interface, backends registered via the existing **keyed DI** pattern (as used for agent
execution services per `AgentTool`). Docker first; Kubernetes later.

```csharp
public interface ISandboxRuntime            // keyed: "docker" | "k8s"
{
    Task<SandboxHandle> ProvisionAsync(SandboxSpec spec, CancellationToken ct);
    Task StopAsync(SandboxHandle handle, CancellationToken ct);
    Task<SandboxStatus> GetStatusAsync(SandboxHandle handle, CancellationToken ct);
}

public sealed record SandboxSpec(
    string Image,
    string RuntimeClass,        // "runc" | "runsc" | "kata-fc"  <- the profile's runtime
    ResourceLimits Limits,      // memory, cpus, pids
    IReadOnlyList<Mount> Mounts,// repo-cache RO, work volume, secrets RO
    EgressPolicy Egress,        // "open" | proxy-allowlist
    IReadOnlyDictionary<string,string> Env); // BackendUrl, EnrollmentToken, Name
```

- `DockerSandboxRuntime` → `RuntimeClass`→`--runtime`, limits→`--memory/--cpus/--pids-limit`,
  mounts→`-v`, egress→`--network`.
- `K8sSandboxRuntime` → `RuntimeClass`→Pod `runtimeClassName`, limits→resource requests/limits,
  mounts→volumes, egress→NetworkPolicy.

The **Sandbox Manager** (new, per host or a K8s controller) owns pool/lifecycle logic written
once against `ISandboxRuntime`; it does not know which backend is live.

### 4.2 Isolation profiles (config default + per-session override)

Three tiers mapping to two runtimes. All are OCI runtimes, so the choice is a single
`RuntimeClass` string both backends understand (`docker --runtime=…`, K8s `runtimeClassName`).

| Profile | Runtime | Runs | Covers |
|---|---|---|---|
| `standard` (default) | `runc`, one strong posture | the **warm pool** | team repos, most sessions |
| `isolated` | `runsc` (gVisor) | on-demand | customer / semi-trusted |
| `strict` | `kata-fc` (Firecracker) | on-demand (snapshot later) | hostile / hard multi-tenant |

`standard` has exactly **one** security posture (rootless, drop-all-caps, tight seccomp, cgroup
caps, egress proxy) so the whole trusted band shares one pool. Resolution precedence:
`session override → workspace default → global config default`, clamped to `AllowedProfiles`.

### 4.3 Pooling policy (one pool, not per-profile)

- Warm-pool **only** `standard`. It covers the majority at ~0 dispatch latency.
- `isolated`/`strict` are **provisioned on demand**; cold start ~0.5–2 s is negligible for
  minutes-long sessions.
- Recycle **one session per container** (one-shot: runner exits after its single `OpenTask`
  completes and the outbox drains; container is `--rm`; the Manager refills the pool). Never
  reuse a Band-2 container across sessions.
- Add a secondary pool for a specific tenant **only** if real traffic proves it — pools are
  demand-driven, never per-profile by default.

### 4.4 Shared substrate (makes every profile's cold start cheap)

The expensive parts are runtime-independent and shared across all profiles:
- **Image** pre-pulled once per host — every runtime reuses the layer cache.
- **Repo mirror** — one bare RO mirror per host at `/repo-cache/<repo>.git`, kept fresh by a
  sidecar (`git remote update --prune`).
- **Secrets** — per-tenant CLI/git creds mounted read-only.

Only runtime boot + runner connect is per-profile.

### 4.5 Repo / worktree provisioning

Ephemeral containers have no persistent clone, so the current `git worktree add` flow needs an
object store. Use **git alternates**: mount the RO mirror, then provision a container-local
parent clone that borrows objects and writes new commits locally:

```bash
git clone --shared file:///repo-cache/<repo>.git /repos/<repo>   # offline, ms, borrows objects
git -C /repos/<repo> remote set-url origin <real-remote-url>       # so the agent can fetch/push
```

The existing host-driven `git worktree add /repos/<repo>/mkwt/<branch>` (RemoteGitOperations)
then runs **unchanged** inside the container — `<parent>` is just a container-local
reference-clone now. Reads come from the RO mirror; the agent's commits land in the writable
container-local `.git`. Don't `git gc --prune` the mirror while sessions hold alternates into it.

Simpler starting variants: fresh shallow clone (no mirror, costs bandwidth + shallow-history
pain) or bind-mounting a host-created worktree (Phase 0; weaker FS isolation).

### 4.6 Networking & credentials

- **Outbound only.** Egress restricted to: API (gRPC + WS + `/api/machines/enroll`), git remotes,
  agent provider APIs, package registries — via a per-host firewall or forced HTTP CONNECT proxy.
  No inbound exposure (the runner dials out).
- **Per-tenant CLI/git creds** injected as read-only mounts or env at launch — never baked into
  the image. Because `HOME` is per-container, there is **zero cross-session credential bleed**
  (a fix over today's shared `~/.claude`).

## 5. Required code changes

**Runner-side (`Mintokei.Runner.Client`)**
- `--one-shot` mode: exit after one `OpenTask` completes and the local outbox drains.
- Container-relative workspace path convention (machines are ephemeral).

**Host-side (`Mintokei.Api` / `Runner.Host`)**
- Endpoint to mint a **short-lived enrollment token** for the Sandbox Manager (reuses the
  `/api/machines/enroll` machinery).
- `Ephemeral` flag + `RuntimeClass`/profile label on `RunnerMachine`, plus GC of offline
  ephemeral machines (so the table doesn't accumulate dead single-use rows). Default
  `MaxInstances=1` for these.
- **Profile-aware machine selection** — a label filter in front of the capacity ledger. *Ship as
  a no-op while `AllowedProfiles=["standard"]`; add the filter when a second profile lands.*

**New**
- `ISandboxRuntime` + `DockerSandboxRuntime` (K8s later), keyed DI by backend.
- `SandboxProfile` config + resolver (`session → workspace → global`, clamped to
  `AllowedProfiles`).
- **Sandbox Manager** service: pool management, launch with limits/mounts/secrets, one-shot
  recycle, repo-cache refresh sidecar.
- The sandbox **image** + CI to build it (runner binary + all agent CLIs on `PATH` + git +
  toolchains, non-root user).

**Ops (not app code)**
- Pre-pull image per host; provision RO repo mirrors; (later) install `runsc`/`kata-fc` as OCI
  runtimes / K8s `RuntimeClass`es.

## 6. Overhead

Plain `runc` adds ~a few–tens of MB RAM per session and near-zero CPU over a bare subprocess;
the agent + its builds dominate and are unchanged. In return you gain per-session cgroup caps
that stop one runaway session from taking down the worker. gVisor adds ~15–50 MB + a syscall/IO
CPU tax; Firecracker adds ~50–130 MB baseline per microVM — paid only by `isolated`/`strict`
sessions, on demand.

## 7. Decision: start with one isolation level, keep the seam open

**Decision.** Implement exactly one runtime now (`standard` / `runc`, one hardened posture, one
warm pool). Build the `SandboxSpec.RuntimeClass` + `ISandboxRuntime` + profile seam so additional
levels are a **config + ops** change later, not a redesign. Do **not** implement gVisor/Firecracker
up front.

**Why.** The cost of multi-level isolation is not the interface (nearly free to keep open) but the
implementations: per-runtime ops install/upgrade on every host, and a toolchain-compatibility
testing matrix per runtime (gVisor has real syscall gaps). Building all three speculatively pays
twice — to build unused runtimes and to remove them later. Adding a level on demand is additive
and localized (new runtime + `AllowedProfiles` entry + the profile-aware selection filter) and
never touches the dispatch/outbox hot path. The only discrete code cost is the **1 → 2 level**
jump (profile-aware selection + per-profile pool accounting); pay it once, when a second level is
justified.

**Seam to keep now (single-valued):** `RuntimeClass` on `SandboxSpec`, a `profile` field on the
session and a matching label on the machine record, and `AllowedProfiles=["standard"]`. Machine
selection stays "any available machine" until a second profile exists.

**Trigger to add `isolated`/`strict`:** the first untrusted / hard-multi-tenant customer. Trigger
to add **Lever 4 (snapshots)** for fast strong-tier starts: metrics show `isolated`/`strict`
sessions are frequent *and* their cold start is a felt problem. Trigger to add the **K8s
backend:** fleet scale outgrows single-host Docker scheduling.

## 8. Phasing

1. **Phase 0** — existing runner in a container, one per workspace, `MaxInstances=N`, no pool,
   bind-mounted worktree. Instant OS isolation between tenants, ~no code. Validates image +
   networking + git provisioning.
2. **Phase 1** — `ISandboxRuntime` (Docker), `standard` warm pool + one-shot recycle, shared
   image/mirror/secrets, profile seam wired single-valued, ephemeral-machine GC. Full
   session-level isolation.
3. **Later, on trigger** — `isolated`/`strict` runtimes + profile-aware selection; snapshot
   restore for strong tiers; K8s `ISandboxRuntime`; per-tenant secondary pools.
