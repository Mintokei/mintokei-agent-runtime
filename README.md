# Mintokei Agent Runtime

Provider-agnostic .NET libraries for **driving coding-agent CLIs** (Claude Code, Codex, Copilot,
OpenCode) and **running them across a fleet of remote worker machines** — the runtime that
powers [Mintokei](https://mintokei.com), extracted to stand on its own with no product coupling.

Every library here is DB-free / product-free: no `Workspace`, no `AgentTask`, no domain event bus.
You embed them and supply your own persistence and orchestration through small callback seams.

> **Status:** pre-1.0 (`0.1.x`). The libraries are in use and tested, but public APIs may change
> between minor releases until 1.0.

## Layers

| Package | What it does | Depends on |
|---|---|---|
| **Mintokei.AgentEngine** | Drive one agent-CLI session over its native stdio protocol — handshake, turns, streaming, interrupts, compaction, permissions — and get a single normalized `AgentMessage` contract across every provider. | logging + DI abstractions only |
| **Mintokei.AgentControlPlane** | Spawn / track many engine sessions with capacity accounting and machine admission. | AgentEngine |
| **Mintokei.Runner.Contracts** (+ `.Grpc`) | Dependency-free wire records + the gRPC/tunnel protocol between backend and worker. | — |
| **Mintokei.Runner.Host** | Backend transport: accept dial-in workers over gRPC, dispatch agent CLIs to them, stream output back over a durable outbox. React to transport events via `IRunnerHost`. | AgentEngine, AgentControlPlane, Contracts |
| **Mintokei.Runner.Client** | The worker side: enroll, hold the gRPC link, run CLIs locally, serve workspace files back over a tunnel. | AgentEngine, Contracts |
| **Mintokei.Runner** | A thin, ready-to-run worker executable over `Runner.Client`. | Runner.Client |

## Which package do I need?

Install with `dotnet add package <name>` — the family is versioned in lockstep, and each layer pulls
in the ones below it, so you never reference a lower package directly.

- **Drive one agent CLI in-process** → `Mintokei.AgentEngine`. That's the whole dependency.
- **Manage many local sessions with capacity limits / admission** → add `Mintokei.AgentControlPlane`.
- **Run agents on remote worker machines** → add `Mintokei.Runner.Host` on the backend, and on each
  worker either `Mintokei.Runner` (ready-to-run executable) or `Mintokei.Runner.Client` (to embed in
  your own host). `Runner.Contracts` / `.Grpc` come along transitively.

## Getting started

```bash
dotnet build Mintokei.slnx
dotnet test  Mintokei.slnx
```

- Drive a single agent CLI: see [`src/Mintokei.AgentEngine/README.md`](src/Mintokei.AgentEngine/README.md).
- Manage many sessions with capacity: see [`src/Mintokei.AgentControlPlane/README.md`](src/Mintokei.AgentControlPlane/README.md).
- Accept remote workers with the smallest possible backend: see
  [`samples/RemoteRunnerMinimal`](samples/RemoteRunnerMinimal).

## Remote runners: how the two halves talk

`Runner.Host` (backend) and `Runner.Client` (worker) are the two ends of one link:

```text
  Backend (Mintokei.Runner.Host)                 Worker (Mintokei.Runner[.Client])
  ──────────────────────────────                 ─────────────────────────────────
  mint one-time enrollment token  ─────────────► enroll → exchange for a machine JWT
  RunnerLinkService   ◄════ gRPC control ══════► hold the link, report presence
  outbox → dispatch a CLI    ────  StartProcess ► run the CLI locally (AgentEngine)
  IAgentSession.Output       ◄──────── stream ── stdout/stderr + files (over a tunnel)
```

A worker dials in on its own: it enrolls with a one-time token, exchanges it for a machine JWT, and
holds a long-lived gRPC control link. The backend registers the runner's presence in the control
plane and dispatches agent CLIs to it through a **durable outbox** (so nothing is lost across a
reconnect); the worker runs each CLI locally via `Mintokei.AgentEngine` and streams output back. To
your code it is the **same `IAgentSession` API as a local spawn** — you just pass a machine id.

## Source of truth

This repository is a **published, buildable view** — the source lives in Mintokei's private
monorepo and is mirrored here on merge. Issues and discussion are welcome; code changes flow from
upstream. See [CONTRIBUTING.md](CONTRIBUTING.md) for how to get a change made.

## License

MIT — see [LICENSE](LICENSE).
