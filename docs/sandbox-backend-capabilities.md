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
that seam is host-agnostic. That decision is sound, but it has a cost worth naming — nothing type-checks the
nested path against the other two, so every shared capability is re-implemented by hand there and can silently
go missing. All three gaps found so far were in local Docker, but the same exposure applies to nested.

## Capability matrix

| capability | interface | local Docker | Kubernetes | nested / remote |
|---|---|---|---|---|
| provision / status / stop / list | `ISandboxRuntime` | ✅ | ✅ | ✅ (own shape) |
| container logs | `ISandboxLogSource` | ✅ | ✅ | ✅ (own method) |
| admission read-back | `ISandboxAdmissionSource` | ✅ | ✅ | ✅ (own method) |
| credential staging for the non-root container | — (a provision step) | ✅ | ✅ init container | ✅ `SandboxCredentialStager` |
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

## Adding a capability

A capability is not done when one backend has it. Before merging, state explicitly for each of the three:
implemented, or deliberately unsupported — and if unsupported, **fail closed and say so**, the way broker
egress does. Silent acceptance of an input that does nothing is the failure mode this document exists to
prevent; it costs far more to diagnose than an outright refusal, because the symptom appears somewhere else
entirely.

Prefer putting the capability behind an optional interface rather than in a provision path. An interface makes
the gap greppable (`grep ': ISandbox'`) and lets `SandboxProvisioner` fail closed on the type check, which is
exactly how the admission gate behaves for a backend that cannot answer.

There are currently **no cross-backend parity tests** — nothing fails when a backend misses a capability. That
is the systemic reason these gaps survive, and the most valuable thing to add here.
