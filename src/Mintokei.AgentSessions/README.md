# Mintokei.AgentSessions

Read and write the on-disk session stores of coding-agent CLIs, normalized to the same
`AgentMessage` contract [`Mintokei.AgentEngine`](https://www.nuget.org/packages/Mintokei.AgentEngine)
emits for live sessions.

`Mintokei.AgentEngine` drives a CLI and reads what it says. This package reads and writes the
transcripts it leaves behind — so a conversation can be moved between CLIs and resumed there.

```csharp
var claude = new ClaudeSessionStore();

await foreach (var s in claude.ListAsync(cwd: "/repo"))
    Console.WriteLine($"{s.SessionId}  {s.FirstUserMessage}");

var session = await claude.ReadAsync(sessionId);
// session.Messages is IReadOnlyList<AgentMessage> — the same type a live IAgentSession emits

var newId = await claude.WriteAsync(session, new SessionWriteOptions { Cwd = "/repo" });
// `claude --resume <newId>` now picks the conversation up
```

Converting between CLIs is then `Read(A) → Write(B)`, once more than one store exists.

## Status

| CLI | Read | Write |
|---|---|---|
| Claude Code | ✅ | ✅ |
| Codex | — | — |
| GitHub Copilot CLI | — | — |

Claude Code only in this release. See **Adding a store** for why the others are not trivial.

## Why `AgentMessage` and not a transfer DTO

A stored transcript and a live stream describe the same thing, so they produce the same type.
One normalization to maintain per CLI instead of two, and an embedder that already handles
`IAgentSession.Output` handles stored sessions with no new code.

`AgentMessage` also carries what a bespoke DTO usually forgets: `ExternalId` (the CLI's own
message id — the thing that makes round-tripping possible), `ToolCallData.ServerName` for MCP
calls, `CommandExecutionData.ExitCode`, and `FileChangeData.Diff`.

### Ids are derived, not minted

A live session mints `AgentMessage.Id` as frames arrive. A file reader has no such moment, so
`SessionIds.Derive` produces them deterministically from the CLI's own ids. Reading the same
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

- **Codex** — `CodexStreamParser` keys off JSON-RPC `method` (`item/completed`, `turn/completed`),
  which is the `codex app-server` protocol. Rollout files have no `method` at all; they use
  `response_item` / `event_msg` / `session_meta`. A new reader is required, plus a `threads` row
  in `state_*.sqlite` for the session to show up in the resume picker.
- **GitHub Copilot CLI** — the engine parses ACP `session/update` notifications, while the store
  speaks Copilot's own event vocabulary. It also validates envelopes strictly (`id` must be a
  UUID, `turnId` is required) and needs rows in `session-store.db` alongside `events.jsonl`.

## What does not survive a write

- **Opaque reasoning** — Codex `encrypted_content`, Copilot `reasoningOpaque`, Claude thinking
  signatures are provider-signed and cannot be reconstructed.
- **Message kinds with no wire form** in the target — `Reasoning`, `Plan`, `FileChange`,
  `CompactBoundary` are written as assistant prose rather than dropped silently.
- **Side channels outside the transcript** — Claude's `file-history-snapshot` (so undo history is
  lost), Codex shell snapshots, Copilot checkpoints and per-session todo databases.

## Failure behaviour

These formats are undocumented and versioned. A transcript that exists but cannot be parsed
throws `SessionStoreException` rather than returning the parsable prefix — silently handing back
half a conversation is the failure that loses data without anyone noticing.

Writes create a new session and never mutate one the caller did not ask for.
