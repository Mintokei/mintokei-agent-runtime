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
| **Mintokei.Filesystem** | Low-level filesystem helpers used by the runner-side file-search and file-watch flows. Most consumers never reference it directly. | — |
| **Mintokei.Runner.Contracts** (+ `.Grpc`) | Dependency-free wire records + the gRPC/tunnel protocol between backend and worker. | — |
| **Mintokei.Runner.Host** | Backend transport: accept dial-in workers over gRPC, dispatch agent CLIs to them, stream output back over a durable outbox. React to transport events via `IRunnerHost`. | AgentEngine, AgentControlPlane, Contracts |
| **Mintokei.Runner.Client** | The worker side: enroll, hold the gRPC link, run CLIs locally, serve workspace files back over a tunnel. | AgentEngine, Contracts |
| **Mintokei.Runner** | A thin, ready-to-run worker executable over `Runner.Client`. | Runner.Client |
| **Mintokei.Sandbox** | Run each agent session in a throwaway, resource-capped container — Docker or Kubernetes — whose in-container runner enrolls back exactly like a remote worker. Per-session OS isolation, isolation profiles (runc / gVisor / Firecracker), an optional warm pool, and one-shot recycle. *Experimental — not yet on NuGet.* | composes with Runner.Host at runtime |

## Which package do I need?

Install with `dotnet add package <name>` — the family is versioned in lockstep, and each layer pulls
in the ones below it, so you never reference a lower package directly.

- **Drive one agent CLI in-process** → `Mintokei.AgentEngine`. That's the whole dependency.
- **Manage many local sessions with capacity limits / admission** → add `Mintokei.AgentControlPlane`.
- **Run agents on remote worker machines** → add `Mintokei.Runner.Host` on the backend, and on each
  worker either `Mintokei.Runner` (ready-to-run executable) or `Mintokei.Runner.Client` (to embed in
  your own host). `Runner.Contracts` / `.Grpc` come along transitively.
- **Reuse the runner's file-search or file-watch filtering rules in your own code** →
  `Mintokei.Filesystem` (advanced; most users do not need this directly).
- **Isolate each agent session in its own throwaway container** → add `Mintokei.Sandbox` on the
  backend. It provisions a per-session container (Docker or Kubernetes) whose runner enrolls back like
  any remote worker, so sessions dispatch through `Runner.Host` unchanged. *Experimental — referenced
  from the runtime rather than installed from NuGet; see [`src/Mintokei.Sandbox/README.md`](src/Mintokei.Sandbox/README.md).*

## Getting started

```bash
dotnet build Mintokei.slnx
dotnet test  Mintokei.slnx
```

- Drive a single agent CLI: see [`src/Mintokei.AgentEngine/README.md`](src/Mintokei.AgentEngine/README.md).
- Manage many sessions with capacity: see [`src/Mintokei.AgentControlPlane/README.md`](src/Mintokei.AgentControlPlane/README.md).
- Embed a worker into your own host: see [`src/Mintokei.Runner.Client/README.md`](src/Mintokei.Runner.Client/README.md).
- Host remote workers in your backend: see [`src/Mintokei.Runner.Host/README.md`](src/Mintokei.Runner.Host/README.md).
- Accept remote workers with the smallest possible backend: see
  [`samples/RemoteRunnerMinimal`](samples/RemoteRunnerMinimal).
- Isolate each session in a per-session container: see [`src/Mintokei.Sandbox/README.md`](src/Mintokei.Sandbox/README.md),
  with runnable [`samples/SandboxSessionMinimal`](samples/SandboxSessionMinimal) (one session end-to-end)
  and [`samples/SandboxPoolMinimal`](samples/SandboxPoolMinimal) (warm pool).

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

## Repository role

This repository is the standalone home of the **Mintokei Agent Runtime** libraries.

Mintokei's private product repository (`Mintokei/mintokei`) consumes this repo as a git submodule at
`external/mintokei-agent-runtime`. Changes to the runtime should land here first; product-side
adoption then happens by bumping that submodule pointer. See [CONTRIBUTING.md](CONTRIBUTING.md) for
the expected workflow.

## License

MIT — see [LICENSE](LICENSE).
