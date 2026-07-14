# ControlPlaneLocal

A local orchestration sample for `Mintokei.AgentControlPlane`. It wires the control plane into a
small DI container, starts one or more local agent sessions, tracks them by caller-owned keys, prints
session lifecycle events, and stops each session after one turn.

## Prerequisites

- .NET 10 SDK.
- At least one supported CLI installed and authenticated on this machine: `claude`, `codex`,
  `copilot`, or `opencode`.

## Run

Start one local session:

```bash
dotnet run --project samples/ControlPlaneLocal -- \
  --tool claude \
  --dir . \
  "Summarise this repository in one sentence."
```

Start two sessions under the same control plane:

```bash
dotnet run --project samples/ControlPlaneLocal -- \
  --tool claude \
  --count 2 \
  --dir . \
  "Summarise this repository in one sentence."
```

By default, CLI permission and question requests are surfaced in the console. For fully trusted local
experiments you can pass `--auto-approve`.

## What it demonstrates

- `services.AddAgentControlPlane()`.
- Registering one `IAgentBackend` and a local `ICommandLineRunnerFactory`.
- Starting sessions under caller-owned keys.
- Observing `SessionStarted`, `SessionEnded`, and `ListSessions()`.
- Using the same `IAgentSession` output vocabulary as `AgentEngine`.
