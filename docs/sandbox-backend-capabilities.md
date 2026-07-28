# Sandbox backend capabilities

What each sandbox backend actually implements, and where they diverge.

This exists because divergence here is **invisible by construction**. There are three backends, only two of
them sit behind `ISandboxRuntime`, and most capabilities are optional interfaces or plain steps in a provision
path. A capability added to one backend and forgotten on another compiles, passes CI, and fails later at a
distance — as a container that dies in its entrypoint, a session that cannot join a sandbox, or a working tree
that quietly does not survive a recycle.

Three such gaps have been found by tripping over them in practice, all the same shape: **local Docker was the
backend that missed out**, every time.

## The three backends

| backend | type | how it runs containers |
|---|---|---|
| local Docker | `DockerSandboxRuntime` | `docker` CLI on this host |
| Kubernetes | `KubernetesSandboxRuntime` | Pods via the k8s API |
| nested / remote | `RemoteSandboxManager` + `RemoteDockerSandboxRuntime` | `docker` on an enrolled worker, over the control channel |

The nested path is **deliberately not** an `ISandboxRuntime`: every method takes the worker's machine id, and
that seam is host-agnostic. That decision is sound, but it has a cost worth naming — nothing type-checks the
nested path against the other two, so every shared capability is re-implemented by hand there and can silently
go missing. Two of the three gaps below were in local Docker, but the same exposure applies to nested.

## Capability matrix

| capability | interface | local Docker | Kubernetes | nested / remote |
|---|---|---|---|---|
| provision / status / stop / list | `ISandboxRuntime` | ✅ | ✅ | ✅ (own shape) |
| container logs | `ISandboxLogSource` | ✅ | ✅ | ✅ (own method) |
| admission read-back | `ISandboxAdmissionSource` | ✅ | ✅ | ✅ (own method) |
| credential staging for the non-root container | — (a provision step) | ✅ | ✅ init container | ✅ `SandboxCredentialStager` |
| persistent workspace at `/repos` | `ISandboxWorkspaceStore` | ❌ **silently ignored** | ✅ PVC | ✅ named volume |
| broker egress | — (a provision step) | ❌ fails closed | ✅ | ✅ |

## Open gaps

### `PersistentWorkspaceKey` is silently ignored on local Docker

`SandboxSpec.PersistentWorkspaceKey` is accepted and has **no effect** on the local Docker backend:
`DockerCommand` never references it, and `DockerSandboxRuntime` does not implement `ISandboxWorkspaceStore`.
`/repos` is therefore the container's ephemeral filesystem.

The consequence is not a launch failure — it is that a recycled session comes back with an empty working tree
and no agent-CLI transcript, so `--resume` fails with *"no conversation found"* long after the fact. The
nested path's own code comments describe this exact outcome as the reason it creates the volume:

> Docker volumes have to be created before `docker run`, hence this step — without it the key would be
> silently ignored on this path and a recycled session would come back with an empty tree and no transcript
> to `--resume` from.

Fixing it means creating a named volume keyed by `PersistentWorkspaceKey` before `docker run` (as the nested
path does) and implementing `ISandboxWorkspaceStore` so a reaper can GC it. Until then, **treat local Docker
as ephemeral-only** and do not rely on resume across a recycle there.

### Broker egress is unsupported on local Docker

`SandboxBrokerWiring.Apply` is called only by `RemoteSandboxManager` and `KubernetesSandboxRuntime`, so
`spec.NetworkName` is never set on the local path and `DockerCommand.BuildRunArgs` refuses to launch:

> broker egress is configured but no per-session broker network is provisioned — refusing to launch
> (fail-closed).

This one is listed as a gap but **not a defect**: it fails loudly, at launch, with an accurate message. That
is the correct handling of an unimplemented capability, and the contrast with the silent case above is the
whole point of this document.

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
