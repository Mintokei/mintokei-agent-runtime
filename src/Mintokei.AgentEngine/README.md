# Mintokei.AgentEngine

A DB-free runtime for driving coding-agent CLIs (Claude Code, Codex, Copilot, OpenCode) over their
native stdio protocols. One `IAgentSession` owns one CLI process plus all the protocol plumbing —
handshake, turns, streaming, interrupts, compaction, rewind, and the permission/question round-trip —
and surfaces the conversation as a pull stream of typed events.

The engine has **no dependency on a database, an ORM, or any host web framework**. It emits plain
message DTOs (`Mintokei.AgentEngine.Contracts`); the embedder decides what to do with them (persist,
stream over SSE, log, …).

## Install

```bash
dotnet add package Mintokei.AgentEngine
```

Or as a `<PackageReference>`:

```xml
<ItemGroup>
  <PackageReference Include="Mintokei.AgentEngine" Version="0.1.0" />
</ItemGroup>
```

Building from a checkout of this repository:

```xml
<ItemGroup>
  <ProjectReference Include="src/Mintokei.AgentEngine/Mintokei.AgentEngine.csproj" />
</ItemGroup>
```

**The engine is fully self-contained — zero project dependencies.** It owns everything it needs:
the session runtime, the agent-tool vocabulary + wire codecs (`Mintokei.AgentEngine.AgentTools`),
and its own process-spawning layer (`Mintokei.AgentEngine.CommandRunner`). The only external
references are the `Microsoft.Extensions.Logging` / `DependencyInjection` **abstraction** packages.

## Quick start

For a runnable console app, see [`samples/LocalAgentMinimal`](../../samples/LocalAgentMinimal).

```csharp
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.Contracts;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.CommandRunner;
using Microsoft.Extensions.Logging.Abstractions;

// LocalCommandLineRunnerFactory spawns the CLI on this machine; swap in your own
// ICommandLineRunnerFactory to run agents on remote machines. Pass any ILoggerFactory.
var factory = new AgentSessionFactory(new LocalCommandLineRunnerFactory(), NullLoggerFactory.Instance);

var spec = new AgentSessionSpec
{
    Tool = AgentToolKey.ClaudeCodeCli,
    WorkingDirectory = "/path/to/repo",
    // Config, SystemPrompt, EnableMcp/McpUrl/McpToken, ResumeSessionId, EnvironmentVariables …
};

await using var session = await factory.CreateClaudeSessionAsync(spec);

await session.SendMessageAsync("Summarise this repository.");

await foreach (var evt in session.Output)          // completes when the CLI stream ends
{
    switch (evt)
    {
        case MessageOutput m:                       // a finished transcript message (AgentMessage DTO)
            Console.WriteLine($"[{m.Message.Role}/{m.Message.Type}] {m.Message.Content}");
            break;

        case DeltaOutput d:                          // real-time token / block / usage delta
            break;

        case TurnEnded te:                           // the agent finished a turn
            if (te.Failure is { } f) Console.WriteLine($"turn failed: {f.StatusLabel}");
            break;

        case InteractionRequested q:                 // CLI is blocked awaiting a permission/answer
            await session.RespondAsync(q.RequestId,
                new UserInteractionResponse(Decision: "allow", Message: null, AnswersJson: null));
            break;
    }
}
```

## Other backends

`CreateClaudeSessionAsync` is a Claude-only shortcut. For Codex / Copilot / OpenCode there's no
one-liner, so do the three steps it does yourself: pick the backend, spawn its process, `Wrap` it into
a session, then `StartAsync` to run the handshake:

```csharp
IAgentBackend backend = new CodexBackend();          // or ClaudeBackend / CopilotBackend / OpenCodeBackend
var runner = runnerFactory.Create(runnerMachineId: null);
var cts = new CancellationTokenSource();
var (handle, output) = runner.Start(backend.BuildCommandLine(spec), cts.Token);

await using var session = factory.Wrap(backend, spec, Guid.NewGuid(), handle, output, cts);
await session.StartAsync(resume: false, CancellationToken.None);
```

## Rehydration: adopting a surviving process

`StartAsync` boots a *freshly-spawned* CLI (runs the `initialize` / `session/new` handshake).
`AttachAsync` is the opposite: it adopts a CLI that is **already running and already handshaken**.

The scenario is a **host restart** — your app (deploy or crash) goes down, but the agent CLI is a
*separate child process* and survives, still mid-conversation and already initialised from before.
Rather than killing it and losing the live turn, re-`Wrap` the surviving process and call
`AttachAsync()` instead of `StartAsync()`: it skips the handshake and picks up exactly where the
previous host left off. (In Mintokei this is session rehydration after an API restart.)

```csharp
// handle/output come from re-opening the surviving process's stdio (not a fresh spawn)
await using var session = factory.Wrap(backend, spec, existingSessionId, handle, output, cts);
await session.AttachAsync();   // adopt: no re-handshake; the CLI keeps its state
```

## The session API (`IAgentSession`)

| Member | What it does |
|--------|--------------|
| `Output` | `IAsyncEnumerable<AgentStreamOutput>` — the one-way transcript; completes at process exit |
| `SendMessageAsync(text)` / `SendTurnAsync(SessionTurn)` | send a user turn (text + optional context block + images) |
| `RespondAsync(requestId, UserInteractionResponse)` | answer a surfaced `InteractionRequested` |
| `InterruptAsync()` | interrupt the in-flight turn; session stays alive |
| `CompactAsync(instructions)` | trigger context-window compaction (where supported) |
| `RollbackAsync(numTurns)` | in-place rewind of the last N turns (Codex; `NotSupported` otherwise) |
| `ApplyConfigAsync(old, new)` | push a mid-session config change (e.g. model switch) |
| `SessionId` / `AgentSessionId` / `HasExited` | the engine's id, the CLI's reported thread id, liveness |

## The output vocabulary (`AgentStreamOutput`)

- **`MessageOutput(AgentMessage)`** — a completed transcript message (text, tool call, file change,
  command execution, compaction boundary, …).
- **`DeltaOutput(DeltaPayload)`** — real-time streaming: content deltas, block start/stop, per-turn
  token-usage snapshots.
- **`TurnEnded(rawResult, isInterrupted, TurnFailure?)`** — a turn boundary; `TurnFailure` normalises
  rate-limit / auth / overload / max-tokens errors across backends.
- **`InteractionRequested(requestId, AgentMessage, cacheKey, notify…)`** — the CLI is blocked on a
  permission / question; answer with `RespondAsync`. (Or auto-handle inline via
  `AgentSessionOptions.InteractionMode = AutoApprove | Deny | Policy`.)
- **`SessionIdChanged`, `ExternalMessageIdAssigned`, `CompactingChanged`, `TurnStarted`,
  `ClearDeltaSnapshot`, `FlushDeltaSnapshot`, `ControlResponseReceived`** — lifecycle / bookkeeping
  signals the host acts on as needed.

## Mapping messages to your storage

The engine emits `Contracts.AgentMessage` (and child DTOs `ToolCallData`, `FileChangeData`,
`CommandExecutionData`, `UserInteractionData`, `CompactBoundaryData`) — pure data, no persistence
concerns. Map them onto whatever your host stores. Mintokei's own embedder does this in a single
`EngineMessageMapper` that copies fields onto an EF entity and translates the contract enums
(`MessageRole/Type/Status`, `FileChangeKind`, `CompactTrigger`) by name.

## Backends

`AgentToolKey`: `ClaudeCodeCli`, `CodexCli`, `GithubCopilotCli`, `OpenCodeCli` (`GeminiCli` reserved).
Copilot and OpenCode share the ACP backend. Each backend owns its launch args, wire protocol, and
interaction reply builder — add a new one by implementing `IAgentBackend` + `IAgentSessionProtocol`.
