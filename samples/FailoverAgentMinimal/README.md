# FailoverAgentMinimal

Run one prompt against a **chain** of coding-agent CLIs. When a turn fails for a reason another
provider could survive — a rate limit, an overloaded or unreachable API, an auth problem — move the
conversation to the next entry in the chain and re-send the turn there.

Two packages meet here, which is the point of the sample:

| | |
|---|---|
| `Mintokei.AgentEngine` | classifies the failure — `TurnEnded.Failure.Kind` |
| `Mintokei.AgentTranscripts` | moves the conversation between the CLIs' own stores |

## Prerequisites

- .NET 10 SDK.
- The CLIs in your chain installed and authenticated. The default chain is `claude,codex`.

## Run

```bash
# see the failover without waiting for a real rate limit
dotnet run --project samples/FailoverAgentMinimal -- \
  --chain claude,codex \
  --simulate rate-limited \
  --prompt "Read notes.txt and tell me the vault value."
```

```
── claude (new conversation)
[claude] Assistant/AgentMessage: I'll read the file.
[claude] Tool/ToolCall: tool: Read
[claude] Assistant/AgentMessage: The vault value is `MARLIN-24`.

[simulated] pretending this turn failed with RateLimited
[claude] Rate limited — simulated failure (--simulate)
   moved 4 message(s), 1 tool call(s) ClaudeCodeCli -> CodexCli as 019fba05-dc83-…
── codex (resuming 019fba05-dc83-…)
[codex] Assistant/AgentMessage: I'll read `notes.txt` now.
[codex] Tool/CommandExecution: $ /bin/bash -lc 'cat notes.txt'
[codex] Assistant/AgentMessage: The vault value is `MARLIN-24`.

done on codex
```

Codex picks up knowing what Claude had already done, because the transcript came with it.

### Options

| Flag | Meaning |
|---|---|
| `--chain <list>` | ordered links, comma separated, each `tool[:model]` (default `claude,codex`) |
| `--prompt <text>` | the prompt to send |
| `--dir <path>` | working directory (default: current) |
| `--simulate <kind>` | force the **first** turn to fail: `rate-limited`, `overloaded`, `api-error`, `auth` |

`--simulate` exists because a demo has to be runnable on demand — real rate limits do not arrive
when you want to show someone what happens. Everything after the failure is the same code path
either way.

## Order the chain: model changes before CLI changes

```bash
dotnet run --project samples/FailoverAgentMinimal -- \
  --chain claude:opus,claude:sonnet,codex --prompt "explain this repo"
```

A **same-CLI** hop only changes the model: the transcript is already in the right store, so the
session is reused untouched and nothing is converted. A **cross-CLI** hop has to read the
transcript out of one store and write it into another, which is where fidelity is lost. Rate
limits are usually per-model, so trying a smaller model on the same CLI first is both cheaper and
lossless.

## What it does not do

- **Retry or back off.** A rate limit that reports `retry-after: 8s` is often better waited out
  than paid for in context fidelity. This sample switches immediately, because it is demonstrating
  the switch.
- **Fail over on everything.** `MaxTokens` means the context is too big everywhere, and
  `SessionNotFound` is deterministic — spending the chain on either just fails slower. See
  `ShouldFailOver`.
- **Translate permissions.** Each CLI keeps its own sandbox and approval semantics; the sample
  runs `InteractionMode.AutoApprove` throughout. A real deployment should decide per link what the
  fallback is allowed to do, because a hop can otherwise widen what the agent may touch.
- **Cover every CLI.** Transcript stores exist for Claude Code and Codex. A hop into or out of
  Copilot or OpenCode still runs — the next link just starts fresh, and the sample says so rather
  than pretending the history came along.
