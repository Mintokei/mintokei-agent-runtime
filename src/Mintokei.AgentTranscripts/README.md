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
- **A compaction boundary.** `system` / `compact_boundary` is a line kind the stream has no reason
  to carry, so the store reads it and hands it to the parser's own
  `ParseCompactBoundaryEvent`. Without that, a moved conversation began with a summary and no sign
  it was one.
- **Questions and plans.** The parser drops `AskUserQuestion` and `ExitPlanMode`/`EnterPlanMode`
  deliberately: a live stream sends each twice, once as a `tool_use` and once as the
  `control_request` the host must answer, and counting both duplicates them. A transcript contains
  no `control_request` — it is a wire frame, never written to the file — so there the `tool_use` is
  the only record. Skipping it deleted the question outright while its answer survived as a tool
  named `unknown`. Measured on a real session with four of them: `unknown=4` before,
  `AskUserQuestion=4` after.

The pattern in all four is the same, and worth naming: **the parser's rules are right for the
stream and wrong for the file.** Anything the CLI sends twice, or sends on a channel that is never
persisted, needs the store to read it instead.

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

## Where the provider gave up

A refused turn — a rate limit, a session limit, an API error — is not something the agent said, but
that is how the CLIs store it: an ordinary assistant message with a flag beside it. Read as prose it
crosses into the next agent's history as a sentence *it* supposedly wrote, which is the wrong lesson
to teach an agent you moved there **because** a provider was failing.

So the parsers classify it as `MessageType.Error`, no writer emits it, and it can be found:

```csharp
var failures = transcript.FindFailures();
foreach (var f in failures)
    Console.WriteLine($"{f.At}  {f.Kind}  {f.Text}  {(f.Recovered ? "(survived)" : "")}");

// the conversation as it stood just before the first one
var atTheFailure = transcript.CutBefore(failures[0]).TrimIncompleteTail().Transcript;
```

`CutBefore` drops the failure and everything after it. The result generally ends mid-turn — the last
thing recorded before a provider gives up is whatever tool call it was in the middle of — so run
`TrimIncompleteTail()` after it rather than duplicating that logic.

`Recovered` is the field to read before cutting anything. A limit is usually a scar rather than an
ending: the person waits for the reset, types `continue`, and the session runs for hours more.
On the session this was built against, both failures had been survived and 3,464 messages followed
the second one.

**Detection** comes from the flag the CLI set, never from the text. A session that spends an
afternoon debugging a 401 is full of messages that say `API Error` and are ordinary conversation.

**Classification** comes from the same line's structured fields, in that order of authority:

| | Claude records | e.g. |
|---|---|---|
| 1. subtype | `"error"` | `rate_limit`, `authentication_failed`, `server_error` |
| 2. HTTP status | `"apiErrorStatus"` | `429` |
| 3. the wording | the message text | "You've hit your session limit · resets 7:40am (UTC)" |

The wording is last on purpose, and it is the one that looks sufficient. One `rate_limit` reaches a
person as both *"Server is temporarily limiting requests"* and *"You've hit your session limit"* —
a vocabulary chasing those sentences is one release behind forever, and neither sentence contains
the phrase "rate limit". The subtype does not move. `AgentMessage.FailureKind` carries what the
parser resolved, because by the time a consumer holds the message the subtype and the status are
gone and only the sentence is left; `Metadata` keeps the raw token for whoever widens the table
next.

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

All of that is **extraction** — no model, no cost, and no understanding of what mattered. When
something has read the conversation and can say what state the work is actually in, pass its
briefing as `Narrative`:

```csharp
transcript.Summarise(new SummaryOptions { Narrative = whatTheReaderSaid });
```

It stands in for the closing message — two accounts of where the work got to, disagreeing, is worse
than either alone — and is labelled as somebody else's reading rather than as something that
happened. The extracted sections stay underneath, because whoever wrote it can be wrong about what
they read and a file list cannot be; `IncludeFacts = false` if you want only the prose.

`hermod --summarise-with <profile>` is this, with an agent CLI as the reader.

The briefing ends on an assistant turn on purpose. A transcript ending on a user turn reads as an
unanswered question — to the next CLI, and to `TrimIncompleteTail`, which would otherwise strip the
briefing that was just built.

This is lossy and is **the wrong default**. Move the real transcript when it fits; reach for this
when the alternative is not fitting at all.

## Checking it against the real CLIs

`FidelityMatrixTests` proves the data survives a crossing. It cannot prove an agent *reads* it
correctly — a file edit crosses as assistant prose rather than a tool result, and whether that
stops the next agent redoing the work is a question only a real agent answers. Nor can a unit test
see the CLIs themselves change, which is where several of these bugs came from.

```bash
scripts/live-check.sh            # every case, every installed CLI
scripts/live-check.sh t6         # just that one
```

| | |
|---|---|
| `t0` | a move into the CLI it came from |
| `t1` | a file edit, which crosses as prose rather than a tool result |
| `t2` | a failed command, whose exit status no format has a field for |
| `t4` | an MCP call moved into a CLI with no such server |
| `t5` | a finding a sub-agent produced |
| `t6` | a run interrupted part-way, finished on another CLI |
| `t8` | unicode, code fences and a 600 KB tool result |

Most plant a value that exists nowhere the target can read, so a correct answer can only come from
carried history. `t6` is the exception and the one worth watching: it kills a five-file edit
part-way, marks the files already done, and fails if the next CLI overwrites them instead of
checking — the difference between continuing work and starting it again.

It spends real tokens; run it after a CLI updates, not on every commit. Not covered: `--attach`,
which needs a terminal to answer the CLI's startup handshake, and a conversation long enough to
trigger auto-compaction.

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

- **Reasoning, as reasoning.** Not because the formats lack a field — Codex has
  `response_item/reasoning` and Claude has thinking blocks — but because none of them replay one
  into model context on resume. Written and checked, all three:

  | injected | session loaded | model saw it |
  |---|---|---|
  | Codex `response_item/reasoning` with a plaintext `summary` | yes | **no** |
  | Claude `thinking` block, empty signature | yes | **no** |
  | Claude `thinking` block, fabricated signature | yes, no API error | **no** |

  That last one is the proof: an invalid signature reaching the API would be rejected, and it was
  not — so the block is never sent. Recorded reasoning is a record, not context. Writing into
  those fields would be worse than prose, because the text would vanish with the session still
  loading cleanly and every file-level test passing.

  So reasoning crosses as an assistant message prefixed `(thinking)`, which is the only channel
  all three replay. `Plan` gets `(plan)` for the same reason.
- **Message kinds with no wire form** in the target — `FileChange` and `CompactBoundary` are
  written as assistant prose rather than dropped. `TranscriptNarration` builds that prose from the
  payload when the message has no `Content` of its own, which is the usual case for an edit.
- **The provider's own failures**, on purpose — a `MessageType.Error` is not written by any store.
  See [Where the provider gave up](#where-the-provider-gave-up); it survives the read so it can be
  found and cut at, and is dropped from the write so no agent inherits it as its own words.
- **Side channels outside the transcript** — Claude's `file-history-snapshot` (so undo history is
  lost), Codex shell snapshots, Copilot checkpoints and per-session todo databases.

## Failure behaviour

These formats are undocumented and versioned. A transcript that exists but cannot be parsed
throws `TranscriptStoreException` rather than returning the parsable prefix — silently handing back
half a conversation is the failure that loses data without anyone noticing.

Writes create a new session and never mutate one the caller did not ask for.
