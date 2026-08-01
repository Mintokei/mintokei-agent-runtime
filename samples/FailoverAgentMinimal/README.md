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
| `--handoff <text>` | what to send after a hop: `default`, `minimal`, or any literal template |
| `--handoff-file <path>` | read the handoff template from a file |
| `--summarise-over <n>` | compress the conversation into a briefing when it exceeds n messages |

`--simulate` exists because a demo has to be runnable on demand — real rate limits do not arrive
when you want to show someone what happens. Everything after the failure is the same code path
either way.

## What the next CLI is told

Re-sending the original prompt is the obvious move and the wrong one: the transferred history
already contains it, so the target sees the same request twice and tends to redo work that may
already have taken effect. Instead the sample trims the turn the agent never finished answering,
and sends a **handoff turn** describing the situation.

The default asks the agent to check before repeating anything — which is the part that does the
work. Against a run where the previous CLI had already applied the edit:

```
[codex] $ sed -n '1,40p' service.yaml
[codex] Checked /root/hd/service.yaml; it already has the requested value: port: 9090
        I didn't need to change anything.
```

Configure it with `--handoff`:

```bash
--handoff default                       # explain + verify (the default)
--handoff minimal                       # "You were interrupted. Continue the work."
--handoff "Interrupted ({failureKind}). Continue: {request}"
--handoff-file ./handoff.txt
```

Placeholders: `{request}` `{reason}` `{failureKind}` `{sourceCli}` `{targetCli}`
`{sourceSessionId}` `{sourcePath}` `{cwd}` `{unresolvedToolCall}`

A line whose placeholder has no value is dropped whole, so a template can mention `{sourcePath}`
and still read correctly when it is unknown. Keep a label on the **same line** as its placeholder —
`Outstanding request: {request}` — because a label on its own line survives when the value does
not, leaving a heading with nothing under it.

`{sourcePath}` is worth including: conversion is lossy, and it tells the agent where to find the
original transcript if it needs a detail that did not survive the crossing.

> **Caveat on `--simulate`:** it injects the failure *after* a turn has completed, so the trimming
> path is not exercised by it — a real mid-turn kill is needed for that. The trim itself is covered
> by unit tests (`TranscriptTrimmingTests`).

## Long conversations

Every hop re-ingests the whole transcript, so a long conversation can overflow the target's context
window. `--summarise-over 200` compresses anything larger into a single briefing — the requests in
order, files touched, recent commands, where the previous agent left off, and a path to the full
transcript for anything omitted.

Lossy on purpose, and off by default: move the real transcript while it fits.

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

## It reacts on the first retry, not the last

Every CLI retries a provider error by itself before giving up. Claude Code defaults to ten attempts
and honours `retry-after`, so a caller that waits for the turn to end learns about a rate limit
minutes after it started. `AgentEngine` surfaces `ApiRetrying` on the **first** failed attempt, and
this sample abandons the turn there:

```
  [claude] RateLimited on attempt 1/10, would wait 45s — not waiting for it to give up
  ...
  done on codex
```

That run took **10 seconds** end to end. Waiting for Claude's own budget would have been ten
attempts at 45 seconds apiece.

For a failure kind the chain would not fail over on, the retry is reported and the CLI is left to
recover on its own.

## What it does not do

- **Back off before switching.** A rate limit reporting `retry-after: 8s` is sometimes better waited
  out than paid for in context fidelity. This sample always switches, because it is demonstrating
  the switch.
- **Fail over on everything.** `MaxTokens` means the context is too big everywhere, and
  `SessionNotFound` is deterministic — spending the chain on either just fails slower. See
  `ShouldFailOver`.
- **Translate permissions.** Each CLI keeps its own sandbox and approval semantics; the sample
  runs `InteractionMode.AutoApprove` throughout. A real deployment should decide per link what the
  fallback is allowed to do, because a hop can otherwise widen what the agent may touch.
- **Cover every CLI.** Transcript stores exist for Claude Code, Codex and GitHub Copilot CLI. A hop
  into or out of OpenCode still runs — the next link just starts fresh, and the sample says so
  rather than pretending the history came along.
