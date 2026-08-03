# Mintokei.AgentTranscripts

Read and write the on-disk session stores of coding-agent CLIs, normalized to the same
`AgentMessage` contract [`Mintokei.AgentEngine`](https://www.nuget.org/packages/Mintokei.AgentEngine)
emits for live sessions.

`Mintokei.AgentEngine` drives a CLI and reads what it says. This package reads and writes the
transcripts it leaves behind — so a conversation can be moved between CLIs and resumed there.

```csharp
var claude = new ClaudeTranscriptStore();

await foreach (var s in claude.ListAsync(cwd: "/repo"))
    Console.WriteLine($"{s.SessionId}  {s.FirstUserMessage}");

var transcript = await claude.ReadAsync(sessionId);
// transcript.Messages is IReadOnlyList<AgentMessage> — the same type a live IAgentSession emits

var newId = await claude.WriteAsync(transcript, new TranscriptWriteOptions { Cwd = "/repo" });
// `claude --resume <newId>` now picks the conversation up
```

Converting between CLIs is then `Read(A) → Write(B)`.

## Transcript vs session

Deliberately not called "session": `Mintokei.AgentEngine` already owns `AgentSession` /
`IAgentSession` / `AgentSessionSpec`, meaning a **live** CLI conversation you can send turns to.
A `StoredTranscript` is the **durable record** of one — inert, readable long after the process
exited, and writable into a store the CLI has never run against.

`SessionId` stays named that on `StoredTranscript`, because it is the CLI's own identifier: the
value you pass to `claude --resume`.

## Status

| CLI | Read | Write | Index |
|---|---|---|---|
| Claude Code | ✅ | ✅ | none needed — the file *is* the session |
| Codex | ✅ | ✅ | `threads` row in `state_*.sqlite` |
| GitHub Copilot CLI | ✅ | ✅ | `sessions`+`turns` in `session-store.db` |

Converting Codex → Claude Code (and back) works today:

```csharp
var source = await new CodexTranscriptStore().ReadAsync(sessionId);
var newId  = await new ClaudeTranscriptStore().WriteAsync(source, new TranscriptWriteOptions { Cwd = cwd });
```

All three read and write, in any direction.

## Why `AgentMessage` and not a transfer DTO

A stored transcript and a live stream describe the same thing, so they produce the same type.
One normalization to maintain per CLI instead of two, and an embedder that already handles
`IAgentSession.Output` handles stored sessions with no new code.

`AgentMessage` also carries what a bespoke DTO usually forgets: `ExternalId` (the CLI's own
message id — the thing that makes round-tripping possible), `ToolCallData.ServerName` for MCP
calls, `CommandExecutionData.ExitCode`, and `FileChangeData.Diff`.

### Ids are derived, not minted

A live session mints `AgentMessage.Id` as frames arrive. A file reader has no such moment, so
`TranscriptIds.Derive` produces them deterministically from the CLI's own ids. Reading the same
transcript twice yields the same Guids — otherwise every re-read looks like a fresh set of
messages to anything downstream that dedupes or resumes an interrupted import.

## Reuse of the engine's parsers

Reading a Claude transcript reuses `ClaudeCodeOutputParser` — the same code that parses live
stream-json — because the file's `user`/`assistant` lines carry the identical `message` envelope.

That reuse is **partial**, and the boundary is worth knowing before assuming it generalises:

- **Plain user turns.** `ParseUserEvent` only reads `tool_result` blocks: in a live stream the
  host already knows the user's turn because it sent it, so the CLI never echoes it back. In a
  transcript the human turns are the whole point, so the store reads those itself.
- **Tool calls arrive twice.** `tool_use` produces an in-progress message and `tool_result` a
  completed one, which the live sink upserts into one row by `ExternalId`. A file reader has no
  sink, so the store collapses them or every tool call appears twice.
- **File-only line kinds** — `attachment`, `file-history-snapshot`, `ai-title`, `last-prompt`,
  `queue-operation` — never appear in the stream and are skipped.

## Adding a store

Claude Code is the easy one: the file *is* the session, and `claude --resume <id>` finds it by
scanning. The others are not the same shape, and the engine's parsers do **not** transfer:

- **Codex** (done, and the shape of the work) — none of the engine's Codex parsing was reusable:
  `CodexStreamParser` keys off JSON-RPC `method` (`item/completed`, `turn/completed`), the
  `codex app-server` protocol. Rollout files carry no `method` at all, using `response_item` /
  `event_msg` / `session_meta` / `turn_context`. Two wire formats for the same conversation.
  Beyond the reader, it needed: skipping `event_msg` (it mirrors `response_item`, so reading both
  doubles every message), filtering `developer` turns and synthetic `<…>` preambles that Codex
  regenerates each launch, joining `function_call` to its later `function_call_output` by
  `call_id`, parsing the exit code out of the `exec_command` output header, and writing a
  `threads` row so the session appears in the interactive picker.
- **GitHub Copilot CLI** (done) — the engine parses ACP `session/update` notifications, while the
  store speaks Copilot's own event vocabulary, so again no reuse. The strictest to write, and it
  fails **silently** — a bad session logs to `~/.copilot/logs/` and exits 1 with nothing on stderr.
  Three things it rejects: an envelope `id` that is not a UUID; a `tool.execution_complete` whose
  `result` is a JSON string rather than an object; and a `workspace.yaml` timestamp in round-trip
  `"o"` format instead of `2026-08-01T14:51:32.508Z`. It also expects `checkpoints/`, `files/` and
  `research/` beside the transcript.

## Long conversations: summarising

Every hop re-ingests the whole transcript, so a long conversation can overflow the target's context
window — and the cost is paid again on each hop.

```csharp
var moved = transcript.SummariseIfLonger(maxMessages: 200);   // untouched when it already fits
await target.WriteAsync(moved, options);
```

`Summarise()` replaces the conversation with a single briefing exchange: where it came from, the
requests in order, files touched, recent commands, and where the previous agent left off — plus the
path to the full transcript, so anything omitted can still be looked up. `SummaryOptions` controls
the limits, whether tool activity is included, and the wording of the header and acknowledgement.

The briefing ends on an assistant turn on purpose. A transcript ending on a user turn reads as an
unanswered question — to the next CLI, and to `TrimIncompleteTail`, which would otherwise strip the
briefing that was just built.

This is lossy and is **the wrong default**. Move the real transcript when it fits; reach for this
when the alternative is not fitting at all.

## What survives, measured

`FidelityMatrixTests` writes one message of each kind into every store and reads it back, so the
table below is checked rather than claimed. A writer that starts carrying more fails those tests
with the row that moved.

| | Claude Code | Codex | Copilot |
|---|---|---|---|
| user / assistant text | ✅ | ✅ | ✅ |
| unicode, code fences, quotes | ✅ | ✅ | ✅ |
| command + output | ✅ | ✅ | ✅ |
| command **exit code** | ✅ | ✅ | ✅ |
| tool name, arguments, result | ✅ | ✅ | ✅ |
| tool error text | ✅ | ✅ | ✅ |
| **MCP server name** | ✅ `mcp__server__tool` | ✅ | ✅ |
| file edit **with** prose | ✅ as prose | ✅ as prose | ✅ as prose |
| file edit **without** prose | ✅ narrated | ✅ | ✅ |
| reasoning | ~ prose, marked `(thinking)` | ~ | ~ |
| large tool result (500 KB) | ✅ | ✅ | ✅ |

What still degrades, and why:

- **A file edit becomes prose.** `Edited /tmp/p/ledger.cs` followed by the diff, rather than an
  edit tool call the target could replay. Deliberate: a diff does not contain the `old_string` /
  `new_string` an edit tool wants, and a fabricated call is a patch the next agent believes it can
  apply. Saying what changed is honest; guessing is not.
- **Reasoning becomes prose, marked `(thinking)`.** It cannot cross as thinking — the signatures
  are provider-issued — but marking it stops a private doubt reading as a claim beside the answer
  that contradicts it.
- **`Plan`, `SubAgentExecution`, `WebSearch` and a compaction boundary** likewise arrive as a
  sentence describing what happened.

Nothing is dropped for want of prose any more; `TranscriptNarration` builds the sentence from
whatever the message carries when it has no `Content` of its own.

## What does not survive a write

- **Opaque reasoning** — Codex `encrypted_content`, Copilot `reasoningOpaque`, Claude thinking
  signatures are provider-signed and cannot be reconstructed.
- **Message kinds with no wire form** in the target — `Reasoning`, `Plan`, `FileChange`,
  `CompactBoundary` are written as assistant prose rather than dropped silently.
- **Side channels outside the transcript** — Claude's `file-history-snapshot` (so undo history is
  lost), Codex shell snapshots, Copilot checkpoints and per-session todo databases.

## Failure behaviour

These formats are undocumented and versioned. A transcript that exists but cannot be parsed
throws `TranscriptStoreException` rather than returning the parsable prefix — silently handing back
half a conversation is the failure that loses data without anyone noticing.

Writes create a new session and never mutate one the caller did not ask for.
