# Sandbox backend capabilities

What each sandbox backend actually implements, and where they diverge.

This exists because divergence here is **invisible by construction**. There are three backends, only two of
them sit behind `ISandboxRuntime`, and most capabilities are optional interfaces or plain steps in a provision
path. A capability added to one backend and forgotten on another compiles, passes CI, and fails later at a
distance — as a container that dies in its entrypoint, a session that cannot join a sandbox, or a working tree
that quietly does not survive a recycle.

Three such gaps have been found by tripping over them in practice, all the same shape: **local Docker was the
backend that missed out**, every time. All three are now closed — credential staging for the non-root
container, admission read-back, and the persisted workspace at `/repos`. They are listed under
[Closed gaps](#closed-gaps) because the pattern matters more than any one of them.

## The three backends

| backend | type | how it runs containers |
|---|---|---|
| local Docker | `DockerSandboxRuntime` | `docker` CLI on this host |
| Kubernetes | `KubernetesSandboxRuntime` | Pods via the k8s API |
| nested / remote | `RemoteSandboxManager` + `RemoteDockerSandboxRuntime` | `docker` on an enrolled worker, over the control channel |

The nested path is **deliberately not** an `ISandboxRuntime`: every method takes the worker's machine id, and
that seam is host-agnostic — at the point a host resolves its single backend there is no "the worker". That
decision stands, but its cost was real: nothing related
`RemoteDockerSandboxRuntime.GetAdmittedToolsAsync(machineId, handle, ct)` to
`DockerSandboxRuntime.GetAdmittedToolsAsync(handle, ct)` — the same operation in two shapes — so a capability
could land on one and go missing on the other with nothing to notice.

`WorkerBoundSandboxRuntime` (`remote.For(machineId)`) pays that off: it binds the machine id and exposes the
ordinary interfaces, so a caller that already knows the worker gets backend-agnostic code, and all three
backends fall under one capability check.

## Capability matrix

| capability | interface | local Docker | Kubernetes | nested / remote |
|---|---|---|---|---|
| provision / status / stop / list | `ISandboxRuntime` | ✅ | ✅ | ✅ (own shape) |
| container logs | `ISandboxLogSource` | ✅ | ✅ | ✅ (own method) |
| admission read-back | `ISandboxAdmissionSource` | ✅ | ✅ | ✅ (own method) |
| credential staging for the non-root container | — (a provision step) | ✅ | ✅ init container | ✅ `SandboxCredentialStager` |
| **staged-credential sweep** | `ISandboxCredentialSweeper` | ✅ | n/a — staged into the Pod's own emptyDir | ✅ |
| persistent workspace at `/repos` | `ISandboxWorkspaceStore` | ✅ named volume | ✅ PVC | ✅ named volume |
| broker egress | — (a provision step) | ❌ fails closed | ✅ | ✅ |

## Open gaps

### Broker egress is unsupported on local Docker

`SandboxBrokerWiring.Apply` is called only by `RemoteSandboxManager` and `KubernetesSandboxRuntime`, so
`spec.NetworkName` is never set on the local path and `DockerCommand.BuildRunArgs` refuses to launch:

> broker egress is configured but no per-session broker network is provisioned — refusing to launch
> (fail-closed).

This one is listed as a gap but **not a defect**: it fails loudly, at launch, with an accurate message. That
is the correct handling of an unimplemented capability, and the contrast with the silent failures below is the
whole point of this document.

## Closed gaps

All three were in local Docker, and all three failed *away* from their cause — which is what made them
expensive. Kept here because the shape repeats.

| gap | how it failed | why it was hard to see |
|---|---|---|
| credentials bind-mounted raw | container ran as a non-root uid it could not read them with; the entrypoint's `cp` died under `set -e` | reported only as *"exited before its agent runner could connect"*, which reads as a clone failure |
| `mintokei.tools` written but never read back | every attempt to join an existing sandbox refused | the label was correct on the container; the *reader* was the missing half, so nothing looked wrong |
| `PersistentWorkspaceKey` accepted and ignored | `/repos` stayed ephemeral, so a recycled session lost its tree and transcript | nothing failed at provision time — only a much later `--resume` |

The third is the purest example: no error, no log line, no failed call. The key was accepted, the container
ran, and the damage only appeared on the next turn after a recycle.

Two more of the same shape were found later, and both are closed:

| gap | how it failed | why it was hard to see |
|---|---|---|
| staged credential copies were never collected | `RemoveAsync` is best-effort, so an interrupted teardown left a real credential on the host with nothing to sweep it | nothing failed at all — and the deployment's token-sync kept refreshing the orphan, so it stayed permanently VALID instead of ageing into a dead token |
| `PersistentWorkspaceKey` honoured differently per backend | Kubernetes created a PVC for a repo-less session; both Docker paths skipped it | both behaviours were locally reasonable; the divergence only appears to an embedder's GC, as a key one backend would never have produced |

The credential one is the worst of the five, because the system was working *against* the cleanup: the sync
that keeps live brokers current had no liveness check, so it maintained an orphaned copy of the model token at
a predictable path for as long as the host ran. Closed on both sides — `ISandboxCredentialSweeper` removes
orphans from reconcile, and [`scripts/sandbox/broker-creds-sync.sh`](../scripts/sandbox/broker-creds-sync.sh)
no longer refreshes a copy whose session is gone.

The workspace-key one is why that decision now lives in `SandboxSpecFactory` rather than in each backend: a
value normalized where the backends converge cannot diverge across them again.

## Adding a capability

A capability is not done when one backend has it. Before merging, state explicitly for each of the three:
implemented, or deliberately unsupported — and if unsupported, **fail closed and say so**, the way broker
egress does. Silent acceptance of an input that does nothing is the failure mode this document exists to
prevent; it costs far more to diagnose than an outright refusal, because the symptom appears somewhere else
entirely.

Prefer putting the capability behind an optional interface rather than in a provision path. An interface makes
the gap greppable (`grep ': ISandbox'`), lets `SandboxProvisioner` fail closed on the type check the way the
admission gate does — and brings it under the parity test below.

## What enforces this

`BackendCapabilityParityTests` asserts every `ISandboxRuntime` implementation implements every capability in
`Capabilities`, or appears in `Exempt` **with a reason**. Add a capability interface and every backend fails
until each has been considered; that is the point. It also asserts the backend discovery itself, since a
backend the test never sees would be exempt from everything by accident — the same silent omission in a new
guise.

Two limits, both worth knowing:

* **It cannot see provision-path capabilities.** Credential staging and broker egress are steps inside
  `ProvisionAsync`, not interfaces, so no type check reaches them. The first of the three gaps was exactly
  that shape and would still slip through. Catching those needs behavioural conformance tests per backend —
  not yet written.
* **It only checks what someone listed.** `Capabilities` is hand-maintained, so a capability nobody adds to it
  is unguarded. Reaching for an interface when you add one is what keeps it inside the net.
