# Sandbox lifecycle

What a sandbox goes through from "provision one" to "it is gone", which states it can be observed in, and
where the three backends genuinely behave differently.

This is the operational companion to [`usage-options.md`](usage-options.md) (which choices exist) and
[`sandbox-backend-capabilities.md`](sandbox-backend-capabilities.md) (which capabilities each backend has).
Read this one when something is stuck, or when you are writing the code that cleans up after sandboxes.

## States

`SandboxState` has five values. They are backend-neutral by design — the pool, the reaper and the wait loop
are all written against them — but each backend derives them from something different, and the *same state can
mean different things*.

| state | Docker (`docker inspect .State.Status`) | Kubernetes (Pod `.status.phase`) |
|---|---|---|
| `Pending` | `created` — a moment between `docker run` returning and the process starting | `Pending` — **scheduling, image pull, volume binding**. Can last minutes, or forever |
| `Running` | `running` | `Running` |
| `Exited` | `exited`, `dead` | `Succeeded`, `Failed` |
| `NotFound` | stderr says `No such object` | API returned 404 |
| `Unknown` | anything else, or the daemon errored | any other phase, or the API errored |

**The asymmetry worth internalising:** on Docker, `Pending` is a blink. On Kubernetes it is where sandboxes go
to die quietly — `FailedScheduling` (not enough CPU on any node) and `ImagePullBackOff` both sit in `Pending`
indefinitely. A sandbox stuck at `Pending` on Docker is a strange bug; on Kubernetes it is the single most
common failure, and `kubectl describe pod` is the answer, not the logs.

`NotFound` is deliberately not an error. A sandbox that has been removed satisfies every caller that wanted it
gone, so cleanup paths treat `NotFound` and `Exited` identically.

## The flow

```
   ProvisionAsync
        │
        ├─ build_request ── mint a one-time enrollment token
        │                   (pre-creates the ephemeral machine id)
        │
        ├─ launch ───────── backend-specific; see below
        │                   state: Pending
        │
        ├─ wait_online ──┬─ pod_ready ────── until state == Running
        │                │                   (scheduling, image pull, volume mount)
        │                │
        │                └─ runner_enroll ── Running → runner connected
        │                                    (entrypoint: clone repos, seed creds,
        │                                     exec runner → dial back → gRPC control stream)
        │
        ├─ ONLINE ──────── dispatch sessions to the machine id
        │                  state: Running
        │
        └─ RecycleAsync ── explicit, never automatic
                           state: NotFound
```

Every phase name above is a real telemetry tag (`sandbox.phase.duration`, tagged `phase` + `backend`), plus an
outcome counter `sandbox.provision.outcome` with `online` / `not_online` / `error`. In practice `wait_online`
is ~99% of provisioning time, which is why it is split: `pod_ready` is infrastructure (your cluster, your
registry), `runner_enroll` is the sandbox's own startup (your image, your repos, your network).

### The two things the wait gets right

**It subscribes before it checks.** `RunnerConnected` is subscribed *first*, then presence is re-checked —
otherwise a runner that connects between the check and the subscribe is never noticed and the provision burns
its whole timeout.

**It polls only to catch death.** The wait is event-driven, but a status poll runs alongside for one purpose:
if the container **exits** during startup, end the wait immediately rather than waiting out the timeout. This
is the difference between "failed in 6 seconds with logs attached" and "failed in 180 seconds with nothing".

On failure the container's logs are read **before** the recycle, and travel on
`SandboxAgentException.ContainerLogs`. A single-shot container is gone the moment it is recycled, and the
reason with it.

## Launch, per backend

### Local Docker

```
stage credentials  ──►  ensure workspace volume  ──►  docker run
(uid-readable copy      (only when the session         (DockerCommand.BuildRunArgs)
 under /tmp)             has repos)
```

The staging step exists because the container runs as uid 10001 and host credentials are root-owned `0600`.
Skipping it produces a container that exits 1 in its entrypoint with a permission error — which reads exactly
like a failed clone.

### Kubernetes

```
[broker: Pod + Service + NetworkPolicies]  ──►  ensure PVC  ──►  create Pod
 (first, so the sandbox can be wired to it;      (409-tolerant:
  torn down again if the Pod fails)               a re-provision REBINDS)
```

That 409 tolerance is what makes resume work: the same workspace key re-attaches to the existing claim, so the
working tree and the CLI transcript survive the recycle.

### Nested (a worker's Docker)

```
probe docker on worker ──► credentials posture ──► build spec ──► [ensure volume] ──► docker run
                            ├─ open/proxy: stage a copy on the worker      (dispatched over
                            └─ broker: start the broker there instead       the control channel)
```

Credentials are resolved against the **worker's** `$HOME`, not the backend host's — the container runs there.

## Teardown

Recycling is always explicit. Nothing disposes a sandbox for you, which is what lets a one-shot run recycle at
the end of a turn while a long-lived product pins the same sandbox across many turns.

| backend | what recycle removes |
|---|---|
| local Docker | container (`docker rm -f`, tolerant of "No such object") + its staged credential copy |
| Kubernetes | Pod (`gracePeriodSeconds: 0`) + the broker's Pod/Service/NetworkPolicies, keyed off the pod name |
| nested | container **+** broker **+** staged credentials, all on the worker |

Two details that are easy to get wrong and are handled:

**Kubernetes tears the broker down unconditionally**, keyed off the sandbox pod's name and 404-tolerant. It is
a no-op for a non-broker sandbox, and it also reaps a broker orphaned by a crash *between* the two creates.

**Nested cleans up broker AND staged credentials — not either/or.** A brokered session still stages a copy for
the broker uid, so treating them as alternatives would leave a model token on the worker after teardown.

## The cleanup loops

Four independent GC paths. They exist because a sandbox can be abandoned in more than one way.

| loop | removes | trigger |
|---|---|---|
| `ReapAsync` | tracked sandboxes whose container has exited | every pool tick |
| `ReconcileAsync` | exited containers **including ones this process never tracked**, then orphaned staged credentials | process start |
| `EphemeralMachineReaper.SelectPrunable` | the machine *rows* of dead sandboxes | embedder's schedule |
| workspace store GC | PVCs / volumes whose key is finished | embedder's schedule |

### The invariant that governs all of them

> **A container's own status decides its fate. Its connection state never does.**

A sandbox that is `Running` but disconnected is a transient partition — the runner reconnects and the session
resumes. Every loop above defers to container existence:

- `ReconcileAsync` leaves running containers alone, even untracked ones.
- `EphemeralMachineReaper` refuses to prune a machine row while its container is in the live list, *and*
  applies a retention window on top, as a grace against the observe-race.
- The credential sweep uses a grace window for the same class of reason in the other direction: credentials
  are staged **before** the container exists, so a sandbox mid-provision legitimately has a copy and no
  container. Without the window, cleanup would delete the credentials of the session that is starting.

### Why staged credentials need a sweep at all

Per-session removal is best-effort — it runs on cleanup paths that must not throw — so an interrupted teardown
leaves a real credential behind. Nothing collected those until `ISandboxCredentialSweeper` existed, and on a
runner that stays up for weeks "it'll go on reboot" is not a policy.

It got worse in combination: deployments that keep a broker's staged token in sync with the rotating host
token refreshed *every* copy they found. An orphan was therefore kept permanently **valid** rather than ageing
into a useless one. Both halves are fixed — the sweep removes orphans, and
[`broker-creds-sync.sh`](../scripts/sandbox/broker-creds-sync.sh) no longer refreshes a copy whose session is
gone.

Kubernetes needs none of this: it stages inside an init container into the Pod's own `emptyDir`, so the copy
is bounded by the Pod's lifetime.

## Warm sandboxes

With `WarmPoolSize > 0`, sandboxes are provisioned **before** anyone needs them and sit `Running`, enrolled,
with no repos. `TryAcquireWarm(profile)` flips one from warm to serving atomically, so it cannot be handed out
twice; the next tick provisions a replacement.

A warm sandbox skips `wait_online` entirely when claimed — it is already connected — which is the whole point:
the wait is the expensive phase.

Warm sandboxes are repo-agnostic, and therefore never carry a persistent workspace store: with no working tree
there is nothing to persist.

## Where it actually goes wrong

| symptom | state | most likely | where to look |
|---|---|---|---|
| never came online, container **exited** | `Exited` | failed clone, unreadable credentials, unreachable backend URL | `ContainerLogs` on the exception — all three name themselves there |
| never came online, still starting | `Pending` (K8s) | `FailedScheduling` (insufficient CPU), `ImagePullBackOff` | `kubectl describe pod` — **not** the logs; there are none yet |
| never came online, still starting | `Pending`/`Running` (Docker) | slow image pull, backend URL unreachable from the container | `docker logs`, then reachability from a container |
| online, but the agent 401s | `Running` | expired/rotated model token | broker token sync; on nested, whether the staged copy is current |
| exits immediately, broker profile | `Exited` | backend host missing from `EgressAllowlist` | provisioning **succeeds** in this case — the broker refuses the CONNECT, so it reads as a startup failure |
| stuck "waiting for a slot" | n/a | admission thinks capacity is used | reaper/registry state, not the sandbox |

The recurring shape: **the failure surfaces far from its cause**, because the container is single-shot and
dials out. Which is why logs are captured before recycling and why the wait ends the moment the container
dies.

## Backend differences that change behaviour

Beyond the state mapping above, these are the divergences that will affect how you operate each one.

| | local Docker | Kubernetes | nested (worker) |
|---|---|---|---|
| executes | `docker` CLI here | k8s API | `docker` on the worker, over the control channel |
| `Pending` means | a blink | scheduling + image pull — **can stall forever** | a blink, on the worker |
| resource *reserve* | advisory (`--memory-reservation`, `--cpu-shares`) | **real**: container `requests`, decides scheduling density | advisory |
| `PidsLimit` | enforced | **ignored** (node-level kubelet setting) | enforced |
| persistence | named volume, created before `docker run` | PVC, created before the Pod, 409-rebind | named volume on the worker |
| workspace removal refused while in use | yes → returns `false` | claim deletion is asynchronous | yes → returns `false` |
| credential staging | host `/tmp`, **needs sweeping** | init container into the Pod's `emptyDir`, dies with the Pod | worker's `/tmp`, **needs sweeping** |
| broker egress | ❌ fails closed at launch | ✅ Pod + Service + NetworkPolicies | ✅ `--internal` network + broker container |
| `ListManaged` includes brokers | **yes** (both carry the managed label) | **no** (excluded by label selector) | yes |
| `AddHostGateway` | honoured | ignored | honoured |
| logs after the sandbox is gone | gone with the container | gone with the Pod | gone with the container |

The `ListManaged` row is the one that catches people writing reconcile code: on the Docker paths the inventory
contains broker containers as well as sandboxes, so anything that treats every entry as a session will try to
reap brokers. Kubernetes filters them out at the API. The credential sweep relies on the Docker behaviour —
passing the full inventory is what keeps a live broker's staged copy from being swept.
