# Usage options

How this runtime can be deployed, as **five independent choices**. Each is orthogonal: pick a value on every
axis and you have a working configuration. Most of what looks like a large configuration surface is these five
choices multiplied together.

Read this before wiring anything. Two of the five (the layer set and the substrate) are decided at
DI-registration time and cannot be changed per session, so getting them wrong means re-wiring rather than
re-configuring.

| Axis | Question | Decided | Values |
|---|---|---|---|
| [1. Layers](#axis-1--layers) | how much of the stack do I take? | code (which `Add*` calls) | engine → control plane → runners → sandbox → sandbox host |
| [2. Substrate](#axis-2--substrate) | where does the container run? | config (`Sandbox:Backend`) at **registration** + per-request for workers | none / local Docker / Kubernetes / a worker's Docker |
| [3. Egress](#axis-3--egress-posture) | what can the sandbox reach, and does it hold secrets? | config, per profile | `open` / `proxy` / `broker` |
| [4. Broker secrets](#axis-4--broker-secret-source) | where do injected credentials come from? | code (which provider is registered) | none / host-read / host-reference / custom / worker-staged |
| [5. Session shape](#axis-5--session-shape) | what does one sandbox look like? | per session | pool, sharing, persistence, isolation, limits |

Axis 4 only exists if axis 3 is `broker`. Everything else composes freely, with the exceptions in
[Combinations that fail closed](#combinations-that-fail-closed).

---

## Axis 1 — Layers

How much of the runtime you take. Each level adds capability and requires the ones below it; you install the
top one you need and the rest come transitively.

### 1a. `Mintokei.AgentEngine` — one CLI, in-process

Drives a single agent-CLI session over its native stdio protocol: handshake, turns, streaming deltas,
interrupts, compaction, permission prompts. Every provider is normalized to one `AgentMessage` contract, so
your storage code doesn't branch per CLI.

**With this alone:** you spawn the CLI as a child process on the machine your code runs on. No database, no
transport, no containers. Dependencies are logging + DI abstractions only.

### 1b. `+ Mintokei.AgentControlPlane` — many sessions, with limits

`AddAgentControlPlane()` plus one `IAgentBackend` per tool and an `ICommandLineRunnerFactory`.

**What changes at runtime:** sessions are tracked under a caller-chosen opaque key, and capacity is enforced
*as you start them* — per-machine counts plus in-flight claims. `StartSessionAsync(key, spec)` either admits
the session or rejects it. The `ICapacityLedger` seam exposes the slot book if you want your own limit logic
or idle eviction.

Still one machine. The control plane is where the *choice* of machine becomes expressible: the same call takes
an optional `runnerMachineId`, which does nothing until you add the next layer.

### 1c. `+ Mintokei.Runner.Host` / `.Client` — a fleet

Backend takes `Runner.Host`; each worker takes `Mintokei.Runner` (ready-to-run executable) or
`Runner.Client` (to embed).

**What changes at runtime:** workers dial *in*. Each enrolls with a one-time token, exchanges it for a machine
JWT, and holds a long-lived gRPC control link. Dispatch goes through a **durable outbox**, so a CLI started
while a worker is briefly disconnected is delivered on reconnect rather than lost. To your code it is the same
`IAgentSession` API as a local spawn — you just pass a machine id.

A working backend is more than one call: a `RunnerHostDbContext`, `AddRunnerHostServer(...)` +
`MapRunnerHost()`, JWT auth validating the `machine_id` claim, `AddAgentControlPlane()`, and a mapped
`RunnerLinkService`. The smallest complete composition is [`samples/RemoteRunnerMinimal`](../samples/RemoteRunnerMinimal).

Your app couples to the transport only through the optional `IRunnerHost` callback interface (runner
connected, CLIs reported, process orphaned, disconnected).

### 1d. `+ Mintokei.Sandbox` — containers

`AddMintokeiSandbox(configuration)` registers `SandboxProfileResolver`, `SandboxSpecFactory`, a single
`ISandboxRuntime` chosen from `Sandbox:Backend`, and `SandboxManager`.

**What changes at runtime:** you can provision a container per session. `SandboxManager` owns lifecycle —
provision, one-shot recycle, reap vanished sandboxes, top up a warm pool — written once against the
`ISandboxRuntime` seam, so it does not know which backend is live.

What this layer does *not* do: mint the enrollment token, wait for the runner inside to come online, or
dispatch the session. That is yours. [`samples/SandboxLifecycleExplicit`](../samples/SandboxLifecycleExplicit)
writes every one of those steps out by hand.

### 1e. `+ Mintokei.Sandbox.Hosting` — the whole loop in one call

`builder.AddSandboxAgentHost().AddClaude()` + `app.MapSandboxAgentHost()` composes the database, transport,
JWT, control plane, gRPC and the sandbox layer. Then `host.RunAsync(request)` runs the entire loop.

**What `SandboxProvisioner` does that hand-wiring usually gets wrong:**

- **Mints the identity first.** The enrollment token pre-creates the ephemeral machine id, so the session
  binds to a *known* id instead of discovering the runner by name after it enrolls. Several sandboxes can
  start concurrently without racing.
- **Waits on an event, not a poll.** It subscribes to `RunnerConnected` *before* re-checking presence (closing
  the race where the runner connected in between), with a status poll alongside purely to notice a container
  that **dies** during startup — so a failed clone ends the wait immediately instead of burning the full
  timeout.
- **Attaches the logs to the failure.** Container logs are read *before* recycling, and travel on
  `SandboxAgentException.ContainerLogs`. Without this the single-shot container is gone and the reason with it.
- **Recycles what it launched** when the wait fails — including the broker and staged credentials on the
  worker path.
- **Emits per-phase telemetry:** `provision.total`, `build_request`, `launch`, `wait_online`, with the wait
  split into `pod_ready` (scheduling, image pull, volume mount) and `runner_enroll` (clone, broker wait,
  control-stream connect). That split is what tells you whether a slow start is infrastructure or your image.

The lifetime stays yours: `ProvisionedSandbox.RecycleAsync` is explicit, never automatic. A one-shot run
recycles at the end of a turn; a long-lived product pins the same sandbox across many turns.

---

## Axis 2 — Substrate

Where the container actually runs. `Sandbox:Backend` is read at **DI-registration time** — one backend per
host process, not a per-call choice. An unknown value throws at startup with the valid list.

The remote-worker path is the exception: it is not a `Backend` value but a **per-request** choice
(`HostMachineId`), so a host can use its local backend for most sessions and a worker for others.

### 2a. No sandbox

Sessions run directly on the host process's machine or on a worker. No isolation, no per-session container.
Valid and common for trusted, single-tenant use.

### 2b. Local Docker — `Sandbox:Backend=docker` (default)

`DockerSandboxRuntime` shells out to the `docker` CLI on the machine running your backend.

**Provision does three things in order:** stage credentials → ensure the persistent workspace volume →
`docker run`. The generated argv is a pure function of the spec (`DockerCommand.BuildRunArgs`, unit-tested
without a daemon):

```
run --detach --name <name> --runtime <runc|runsc|kata-fc>
    --memory <bytes> --cpus <n> --pids-limit <n> [--memory-reservation …] [--cpu-shares …]
    --cap-drop ALL --security-opt no-new-privileges [--read-only]
    --label mintokei.sandbox=1 [--label mintokei.tools=…]
    --tmpfs /data:uid=10001,gid=10001,mode=0700
    [--network <per-session>] [--add-host host.docker.internal:host-gateway]
    [--volume …] [--env …] <image> --backend … --token … --name …
```

**The credential staging step matters more than it looks.** The container runs as uid 10001, and host CLI
credentials are normally root-owned `0600`. Mounting them raw means the entrypoint's copy fails with
*permission denied* and the container exits 1 — far from its cause. The runtime stages a uid-readable
per-session copy and mounts that instead, removed with the container. You do **not** need to loosen
permissions on your own credentials.

Implements all three optional capabilities (logs, admission read-back, persistent workspace as a named
volume). **Does not support broker egress** — see [2e](#2e-broker-egress-on-your-own-machine).

### 2c. Kubernetes — `Sandbox:Backend=kubernetes` (alias `k8s`)

`KubernetesSandboxRuntime` talks to the API server with the typed client. One Pod per session in
`Sandbox:KubernetesNamespace`. No docker socket, no CLI in the image.

**Which cluster,** by precedence — so the sandbox substrate can be a *different* cluster from where your
control plane runs:

1. `KubernetesApiServerUrl` + `KubernetesToken` (no kubeconfig file needed)
2. `KubernetesKubeconfig` (+ optional `KubernetesContext`) — the usual way to target a dedicated cluster
3. default: in-cluster ServiceAccount when running as a Pod, else the ambient kubeconfig (dev / k3d)

**Behavioural differences from Docker that will surprise you:**

- **The reserve becomes real.** `MemoryReserveMb` / `CpuReserve` map to container *requests*: a scheduling
  guarantee that decides how many sandboxes fit a node. On Docker the same values are advisory
  (`--memory-reservation` is a soft limit, `--cpu-shares` a weight). Set them too high here and you get
  `FailedScheduling` under load — not an error at startup.
- **`PidsLimit` is ignored.** Kubernetes has no per-Pod field; it is a node-level kubelet setting.
- **Persistence is a PVC**, `ReadWriteOnce`, created before the Pod references it and 409-tolerant, so a
  re-provision of the same key rebinds the existing claim. That rebind is what preserves the working tree and
  the CLI transcript across a recycle.
- **Admission is a pod annotation** rather than a container label.
- **`ListManagedAsync` excludes broker Pods** (they carry the managed label too) so the reaper never treats a
  broker as a reclaimable session.
- **`AddHostGateway` is ignored** — use the Service URL.

Set `KubernetesImagePullPolicy=Never` when the image is node-imported rather than pulled, or a `:latest` tag
will trigger a failing pull of a private image.

### 2d. A worker's Docker (nested / remote)

Per-request: set `SandboxProvisionRequest.HostMachineId`, or call `RemoteSandboxManager.LaunchAsync(workerId, …)`
directly. Requires `AddRemoteWorkers()` (or `AddMintokeiRemoteSandbox()`), and a worker that is **connected**
and **Docker-capable** — both are probed, and both fail with a specific message rather than a timeout.

**What differs from local Docker:**

- **`docker run` is dispatched over the worker's control channel**, not executed here.
- **Credentials live on the worker.** Any credential path left unset is defaulted from the *worker's* `$HOME`
  (probed over the link), not from your backend's paths. They are then staged uid-readably on the worker.
- **The persistent volume is created before `docker run`** — Docker cannot create one implicitly the way
  Kubernetes creates a PVC from the Pod spec. As on local Docker, it is created **only when the session has
  repos**: with no working tree there is nothing to persist.
- **Disposal cleans up three things on the worker:** container, broker (if any), staged credentials. Not
  either/or — a brokered session stages a copy for the broker uid, so skipping the stager would leave a model
  token behind after teardown.

This path is deliberately **not** an `ISandboxRuntime`: every method takes a machine id, and at the point a
host resolves its single backend there is no "the worker". When you already know the worker, wrap it with
`remote.For(machineId)` to get an ordinary `ISandboxRuntime` + all three capability interfaces
(`WorkerBoundSandboxRuntime`), so callers stay backend-agnostic.

### 2e. Broker egress on your own machine

There are **two local-Docker paths**, and the obvious one cannot do broker egress:

| | `Sandbox:Backend=docker` | `AddMintokeiLocalCommandRunner()` |
|---|---|---|
| runtime | `DockerSandboxRuntime` | `RemoteDockerSandboxRuntime` |
| worker needed | no | no |
| broker egress | ❌ fails closed at launch | ✅ |

`AddMintokeiLocalCommandRunner()` replaces `IRemoteCommandRunner` with a local process runner, pointing the
*remote* path at this machine's daemon. **Registration order matters:** call it *after* anything that provides
the gRPC dispatcher (`AddMintokeiRunnerHost`), or that registration wins.

Single-host and local-dev only. See [`samples/BrokerSandboxMinimal`](../samples/BrokerSandboxMinimal).

---

## Axis 3 — Egress posture

Per profile: `Sandbox:Profiles:<name>:Egress`. Decides **both** what the sandbox can reach **and** whether it
holds credentials at all — the two are the same decision, which is why one enum controls them.

An unrecognized value resolves to `open` rather than failing. The `EgressAllowlist` is *dropped* unless the
posture is `broker`, so a resolved profile only ever reflects what is actually enforced.

### 3a. `open` (default)

Unrestricted network. Credentials are seeded: the configured host paths are mounted read-only under `/seed`
and the entrypoint copies them into the container's HOME.

**Use when** the sandbox boundary is about resource isolation and blast radius, not about protecting the
credentials from the code running inside.

### 3b. `proxy`

Sets `HTTP_PROXY` / `HTTPS_PROXY` to `EgressProxyUrl`. Credentials are still seeded.

**This is advisory and you should treat it as such.** Only clients that honour the proxy env vars are routed;
anything that ignores them has an unchanged route out. There is no network-level enforcement, and the
allowlist is not consulted. It is a convenience for well-behaved tooling, not a containment boundary.

### 3c. `broker` — the hardened posture

Two things change at once: **no long-lived secret enters the container**, and **the network is the
enforcement**.

The sandbox joins a per-session deny-by-default network (Docker: `--internal`; Kubernetes: NetworkPolicies)
whose only reachable peer is that session's broker container. A process that ignores the proxy env still has
no route out. The broker holds the real credentials and re-originates calls over TLS with the real auth added.

`SandboxBrokerWiring.Apply` then wires the sandbox to reach it:

- `MINTOKEI_BROKER_CRED_URL` → the git-credential mint
- each configured model provider's base URL → its broker port, plus a **placeholder** credential (the CLI
  won't attempt a call without one; the broker replaces the auth header upstream, so the placeholder never
  leaves)
- `COPILOT_DEBUG_GITHUB_API_URL` + a format-valid `github_pat_` placeholder (Copilot validates the format
  locally before any network call)
- `NO_PROXY` = the broker's own hostname — its mint/model/github services are *plaintext*, and a client
  honouring `HTTP_PROXY` would otherwise forward them through the CONNECT proxy, which only does CONNECT

**Five fail-closed checks** guard this posture — it refuses to launch rather than launch unenforced:

| Check | Where | Message shape |
|---|---|---|
| allowlist non-empty | spec factory | "…allowlist is empty (neither profile nor session supplied one)" |
| `AddHostGateway` off | spec factory | "…incompatible with AddHostGateway (host reachability defeats containment)" |
| backend URL is `https://` | spec factory | "…CONNECT proxy that only tunnels TLS — set an https:// GrpcBackendUrl" |
| an `ISandboxBroker` is registered | manager / K8s runtime | "…but no ISandboxBroker is registered" |
| a per-session network exists | `DockerCommand` | "…no per-session broker network is provisioned" |

The `https` requirement is a **deployment constraint, not a config detail**: .NET only CONNECT-tunnels TLS, so
a plaintext backend URL cannot traverse the broker at all. Configure `PublicBackendUrl` /
`PublicGrpcBackendUrl` and the runtime swaps them in automatically for brokered sessions — that is how a
cluster-internal deployment reaches its own control plane from inside a brokered sandbox.

**What is *not* enforced:** that your backend's host appears in `EgressAllowlist`. Only non-emptiness is
checked, because the allowlist is opaque host matching and the URL may legitimately use a different name. Omit
it and provisioning **succeeds** — then the broker refuses the CONNECT and the runner never enrols, which
reads as a startup failure rather than a config mistake. Check this by eye.

The sandbox must also trust the certificate. There is no skip-verify switch and no per-session CA injection;
behind an internal CA, bake it into your own sandbox image.

**Per-session allowlists win over the profile's.** One `broker` profile can serve tools with different egress
needs — the alternative is a profile per tool or an allow-all list, and both are worse.

---

## Axis 4 — Broker secret source

Only consulted when axis 3 is `broker`. Determines where the credentials the broker injects come from — the
one piece a product-agnostic runtime cannot own, because it depends on your identity model and secret store.

A session declares its **needs** (`SandboxBrokerNeeds`: which model providers, whether git, whether a GitHub
token, plus its allowlist) in the sandbox layer's own vocabulary — provider names, not agent tools. Only what
is declared gets injected.

### 4a. None — `NoSandboxBrokerSecrets` (default)

Registered via `TryAdd`, so it applies unless you register something else. Returns null.

**Containment still holds** — deny-by-default egress is fully in effect. Nothing is injected, so the agent CLI
inside will fail authentication. Useful for testing the network posture in isolation; not a working session.

### 4b. Host-read — `AddMintokeiHostCredentialsBrokerSecrets()`

Reads the standard credential files from the `Sandbox:BrokerCredentials` locations (`AnthropicDir`,
`OpenAiDir`, `GitDir`, `GitHubToken`) and builds the provider-specific header shapes for you.

**Requires the host process to be able to read those files.** A provider that is unknown, or configured but
unreadable, is logged and **skipped** — the launch is not failed, on the grounds that a partially-credentialed
brokered session is still contained.

### 4c. Host-reference — `AddMintokeiHostCredentialsFileRefBrokerSecrets()`

Same locations, but emits `${json:…}` / `${gitcreds:…}` **references** plus the directories to mount, and the
broker resolves them itself at startup.

**This is the Kubernetes answer**, and it exists for a concrete reason: the API pod runs non-root and cannot
read `0600` root-owned node credentials. With references, the token never touches your control plane and never
becomes a Kubernetes Secret.

### 4d. Custom — `AddMintokeiSandboxBrokerSecrets<T>()`

Your own `ISandboxBrokerSecretsProvider`, called at provision time with the session request and resolved
profile. This is the seam for **per-tenant** credentials. Build the result with the convention helpers
(`ModelUpstreamSpec.AnthropicOAuth`, `SandboxBrokerSecrets.GitCredentialLine`, …) so you never re-derive header
formats.

### 4e. Worker-staged (automatic, no registration)

Not a provider. On the remote path, when a session declares broker needs and no explicit secrets were passed,
`RemoteSandboxManager` stages a per-session copy of the *worker's own* credentials for the broker uid (10002),
mounts it into the broker only, and builds references from it. The token is read broker-side on the worker and
never crosses the control plane.

Precedence on that path: **explicit argument** → **worker-staged from needs** → **registered provider**.

---

## Axis 5 — Session shape

Per-session choices. All optional, all independent of the axes above.

### On-demand vs. warm pool

`WarmPoolSize > 0` + `AddMintokeiSandboxPool()` + your own `ISandboxSessionSource` (which supplies each warm
sandbox's enrollment token, backend URL and unique name — the pool loop stays free of any enrollment
dependency).

**At runtime:** a background service reconciles once at startup, then every `PoolIntervalSeconds` reaps exited
sandboxes and tops the pool back up. `TryAcquireWarm(profile)` atomically flips a warm sandbox to serving so it
cannot be handed out twice; the next tick provisions a replacement. Warm sandboxes are **repo-agnostic** —
they have no working tree until a session claims them.

Dormant at `0`, which is the default.

Reconcile deliberately removes only **exited** containers, including ones this process never tracked (leftovers
from a previous run). A running-but-disconnected sandbox is left alone: that is a transient partition its
runner will reconnect through, and connection state is never a reason to kill a container.

### One session vs. shared

Set `AdmittedTools` and the sandbox is stamped with them (container label / pod annotation). A second session
joining must pass `EnsureCanAttachAsync`, which reads the declaration **back off the infrastructure**.

**Why read it back rather than trust your own record:** your record can be stale — after an API restart, a DB
rollback, or under a second embedder. The only authority on what a running broker will actually serve is the
sandbox itself.

Sharing is refused in two cases that both look like "it should have worked":

- **The backend cannot report a declaration** (no `ISandboxAdmissionSource`) — nothing to check against, so
  sharing is unavailable rather than assumed safe.
- **The existing sandbox carries no declaration** — it is single-session, provisioned by a caller that never
  opted into sharing. This is the one place an empty declaration does *not* mean "unconstrained".

The reason admission is enforced in the library rather than left to callers: a sandbox has **one** broker on
**one** network, and the broker cannot attribute a connection to a session. Its allowlist and injected
credentials apply to every session inside. Admitting a tool the sandbox wasn't built for silently grants
*every* session in it that tool's reach, for the sandbox's whole life — and the allowlist is fixed when the
broker starts, so it cannot be corrected without recycling.

Also set `LimitsOverride` when sharing: N sessions in one container share **one cgroup**, so a memory limit
sized for a single session means the first overrun OOM-kills all of them.

### Ephemeral vs. persistent workspace

`PersistentWorkspaceKey` backs `/repos` with a store named `mintokei-ws-<key:N>` — a PVC on Kubernetes, a named
volume on Docker — labelled `mintokei.task`.

**What this buys:** the working tree *and* the agent-CLI transcript (symlinked onto `/repos` by the entrypoint)
survive a recycle. That is the difference between a reaped session that **re-provisions** and one that
**actually resumes** with `--resume`.

The key is **opaque** — the runtime only names and labels the store with it. Key by whatever owns the working
tree in your product: one per task, or one per workspace shared by every task in it.

**One divergence between backends:** both Docker paths skip the store entirely when the session has no repos
(no working tree, nothing to keep), while Kubernetes creates the PVC whenever the key is set. So a
repo-less session leaves an empty PVC behind on Kubernetes and nothing on Docker — your GC sees a key the
Docker backends would never have created.

GC is yours (`ListPersistentWorkspaceKeysAsync` / `RemovePersistentWorkspaceAsync`). Removal returns **false**
when the store survived — including when a live container still mounts it — so a caller mirroring the deletion
into its own state never acts on a store that is still there.

### Isolation runtime

`Runtime` = `runc` (default) | `runsc` (gVisor) | `kata-fc` (Firecracker microVM) → one knob on both backends
(`--runtime` / `runtimeClassName`). The node must actually have that runtime installed; the runtime does not
verify it.

### Read-only rootfs

`ReadOnlyRootfs` adds `--read-only` / `readOnlyRootFilesystem`, and HOME, `/tmp`, `/data` and `/repos` become
writable tmpfs. Where a real mount exists at the same path (the persistent volume at `/repos`), the mount wins
— Docker rejects a tmpfs and a volume at one path.

Off by default, and genuinely opt-in: it only adds defence over the non-root default for paths the agent user
could otherwise write in the image, and it **breaks** agents that write outside the writable set (`apt-get`,
build tools writing to system dirs).

### Resource limits

`MemoryMb` / `Cpus` / `PidsLimit` are the ceiling — exceed memory and the container is killed, exceed CPU and
it is throttled, identically on both backends.

The **reserve** (`MemoryReserveMb` / `CpuReserve`) is deliberately not called "request": it is a real
reservation on Kubernetes and advisory on Docker (see [2c](#2c-kubernetes--sandboxbackendkubernetes-alias-k8s)).
Null derives it — half the memory limit capped at 1 GiB, a quarter of the CPU limit. **Leave it null unless
you have measured**: it is the knob that decides scheduling density.

### Repo cache

`RepoCacheHostPath` mounts a bare-repo mirror read-only at `/repo-cache`, and clones borrow its objects.
Applied only when the session has repos.

---

## Combinations that fail closed

Every one of these refuses with a specific message rather than launching something weaker than you asked for.

| Combination | Outcome |
|---|---|
| `broker` + `Sandbox:Backend=docker` | refused at `docker run` — no per-session network is provisioned on that path |
| `broker` + `AddHostGateway` | refused — host reachability defeats containment |
| `broker` + `http://` backend URL | refused — the CONNECT proxy only tunnels TLS |
| `broker` + empty allowlist | refused — an unbounded "brokered" sandbox is worse than none |
| `broker` + no `ISandboxBroker` registered | refused |
| sharing + backend without `ISandboxAdmissionSource` | refused — nothing to check against |
| sharing + sandbox carrying no declaration | refused — it is single-session |
| `HostMachineId` + no `AddRemoteWorkers()` | `SandboxAgentException` naming the missing call |
| `HostMachineId` + worker not connected / no Docker | refused, probed before anything is created |
| unknown `Sandbox:Backend` | throws at startup, listing the valid values |

**The one thing that does not fail closed:** your backend's host missing from `EgressAllowlist` under broker
egress. Provisioning succeeds and the failure appears later, as a sandbox that never enrols. Check it by eye.

---

## Where each choice is made

| Choice | Registration-time | Config | Per-session |
|---|---|---|---|
| Layers | ✅ which `Add*` calls | — | — |
| Backend (docker / k8s) | ✅ read from config *at registration* | ✅ `Sandbox:Backend` | ❌ one per process |
| Run on a worker | ✅ `AddRemoteWorkers()` | — | ✅ `HostMachineId` |
| Egress posture | — | ✅ per profile | ✅ via profile choice + allowlist |
| Broker secrets | ✅ which provider | ✅ locations | ✅ declared needs |
| Pool | ✅ `AddMintokeiSandboxPool()` | ✅ `WarmPoolSize` | — |
| Sharing / persistence / limits | — | ✅ defaults | ✅ per request |

---

## Samples by combination

| Combination | Sample |
|---|---|
| 1a — one CLI | [`LocalAgentMinimal`](../samples/LocalAgentMinimal) |
| 1b — many local sessions | [`ControlPlaneLocal`](../samples/ControlPlaneLocal) |
| 1c — remote workers, no containers | [`RemoteRunnerMinimal`](../samples/RemoteRunnerMinimal) |
| 1d — sandbox lifecycle, no infrastructure | [`SandboxSessionMinimal`](../samples/SandboxSessionMinimal) |
| 5 — warm pool, no infrastructure | [`SandboxPoolMinimal`](../samples/SandboxPoolMinimal) |
| 1e + 2b + 3a — the default real setup | [`SandboxRunnerHostMinimal`](../samples/SandboxRunnerHostMinimal) |
| 1d + 2b + 3a — every step by hand | [`SandboxLifecycleExplicit`](../samples/SandboxLifecycleExplicit) |
| 5 — two sessions sharing one sandbox | [`SharedSandboxMinimal`](../samples/SharedSandboxMinimal) |
| 2d + 3a — a worker's Docker | [`RemoteSandboxMinimal`](../samples/RemoteSandboxMinimal) |
| 2d/2e + 3c + 4 — broker egress | [`BrokerSandboxMinimal`](../samples/BrokerSandboxMinimal) |

**No sample covers Kubernetes** (2c), the `proxy` posture (3b), persistence (axis 5), or a warm pool against a
real backend. Those are configuration-only changes to the samples above, but they are unexercised here.

See [`samples/README.md`](../samples/README.md) for what each one needs before it will run, and
[`sandbox-backend-capabilities.md`](sandbox-backend-capabilities.md) for which capabilities each backend
actually implements.
