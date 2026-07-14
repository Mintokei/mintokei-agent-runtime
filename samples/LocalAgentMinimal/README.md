# LocalAgentMinimal

The smallest local use of `Mintokei.AgentEngine`: start one coding-agent CLI process on the current
machine, send one prompt, print transcript events until the turn ends, and dispose the process.

## Prerequisites

- .NET 10 SDK.
- At least one supported CLI installed and authenticated on this machine: `claude`, `codex`,
  `copilot`, or `opencode`.

## Run

```bash
dotnet run --project samples/LocalAgentMinimal -- \
  --tool claude \
  --dir . \
  "Summarise this repository in three bullets."
```

The default tool is `claude`. Use `--tool codex`, `--tool copilot`, or `--tool opencode` to try another
backend.

```bash
dotnet run --project samples/LocalAgentMinimal -- --tool codex --dir . --prompt "List the public packages."
```

By default, CLI permission and question requests are surfaced in the console. For fully trusted local
experiments you can pass `--auto-approve`.

## What it demonstrates

- `AgentSessionFactory` with `LocalCommandLineRunnerFactory`.
- Backend selection through `IAgentBackend`.
- One `AgentSessionSpec` with a working directory and optional config.
- Consuming `MessageOutput`, streaming content deltas, `InteractionRequested`, and `TurnEnded`.
