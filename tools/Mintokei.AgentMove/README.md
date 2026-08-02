# agentmove

Pick a session from one agent CLI and carry on with it in another.

Run it in the directory the work happened in. It lists what each CLI recorded there — by
description, not by id — and moves the one you choose into whichever agent you want to continue in.

```
$ agentmove

directory  /repo
config     ./agentmove.json

Copy from:
  [1] Claude Code  (7 session(s))
  [2] Codex        (2 session(s))

Source [1-2, or q to quit]: 1

Sessions in /repo:

  [ 1] 2m ago       Read cfg.yaml and find port number
       e9b8e444-715c-4886-a9a4-1a503b68ee55
  [ 2] 3h ago       Fix the failing ledger test
       12eaecc0-0396-41e6-8782-f4e9a4e0bb40

Session [1-2, or q to quit]: 1

Continue as:
  [1] claude-fast   claude/claude-sonnet-4-5  — smaller model, accepts edits
  [2] codex         codex/gpt-5.5             — asks before acting outside the workspace

Target [1-2, or q to quit]: 2

  Claude Code  ->  codex (codex/gpt-5.5)
  permissions: approvalPolicy=on-request

Proceed? [y/N]: y

  moved 3 message(s) as 019fbd5b-6d54-7ccf-adea-cc8dd9dcf1bc

Resume it with:  codex resume 019fbd5b-6d54-7ccf-adea-cc8dd9dcf1bc
```

It prints a handoff message to paste as your first turn; the history is already there, so it says
where the conversation came from rather than repeating it.

## Or carry on here: `--launch`

`--launch` starts the target CLI itself, sends the handoff turn, and leaves you at a prompt:

```
$ agentmove --launch

  ...
  Claude Code  ->  codex (codex/gpt-5.5)
  start:       agentmove launches it here
  permissions: approvalPolicy=on-request  sandbox=read-only

Proceed? [y/N]: y

  moved 4 message(s) as 019fc18d-c10b-779d-9fb8-a59c10e17676

── codex/gpt-5.5 — resumed 019fc18d-c10b…, sending the handoff turn
   (blank line or /quit to leave; the session stays on disk either way)

I'll check the workspace rather than trusting the handoff history.
  · /bin/bash -lc 'sed -n 1,120p notes.txt'
notes.txt still has `vault: HARRIER-71`. Nothing was left half-done.

> what's on line 2?

  · /bin/bash -lc "sed -n '2p' notes.txt"
`port: 8080`.

>

Session 019fc18d-c10b-779d-9fb8-a59c10e17676
Pick it up again with:  codex resume 019fc18d-c10b-779d-9fb8-a59c10e17676
```

**This is the only path on which the profile's settings are actually in force.** A printed command
can carry what that CLI's own resume invocation accepts and nothing more — for Codex that is
nothing at all, because the engine drives it over `codex app-server` rather than by flags. So
agentmove says which of the two you are getting, and marks the settings the command would drop:

```
  permissions: approvalPolicy=on-request  sandbox=read-only
               ^ NOT applied by the command below (approvalPolicy, sandbox) — use --launch,
                 or set them in the CLI's own settings
```

`extraArgs` is the other way round: it reaches the printed command and *not* `--launch`, because
`AgentSessionSpec` has no verbatim-arguments field — the engine builds the command line from
`config` alone. agentmove says so rather than dropping them quietly.

Leaving the prompt does not end anything. The session is a normal session in the target CLI's own
store, so `codex resume <id>` picks it up in the real TUI whenever you want.

## Configuration

```bash
agentmove --init          # writes ./agentmove.json
```

Read from `--config`, then `./agentmove.json`, then `$XDG_CONFIG_HOME/agentmove/config.json`.
Without one, a conservative built-in profile per supported CLI is used.

```json
{
  "profiles": {
    "claude-fast": {
      "tool": "claude",
      "description": "smaller model, accepts edits",
      "config": { "model": "claude-sonnet-4-5", "permissionMode": "acceptEdits" }
    },
    "codex": {
      "tool": "codex",
      "config": { "model": "gpt-5.5", "approvalPolicy": "on-request" },
      "extraArgs": ["--skip-git-repo-check"]
    }
  },
  "summariseOver": 400
}
```

Under `--launch`, `config` goes straight to `AgentSessionSpec.Config`, which each backend's config
mapper turns into how that CLI is run — so a profile can express anything the engine can launch,
and picks up new keys as the mappers grow:

| Backend | Keys |
|---|---|
| claude | `model` `effort` `maxTurns` `permissionMode` `allowedTools` `systemPromptFile` `allowDangerouslySkipPermissions` `verbose` |
| codex | `model` `modelProvider` `modelVerbosity` `effort` `summary` `personality` `collaborationMode` `approvalPolicy` `sandbox` `webSearch` `ephemeral` `noProjectDoc` |
| copilot | `model` `effort` `mode` `allowAllPaths` `disableAskUser` `disableBuiltinMcps` `enableAllGithubMcpTools` `maxAutopilotContinues` |
| opencode | `model` `agent` `dangerouslySkipPermissions` |

A key outside its backend's list is an **error**, not a shrug:

```
profile 'codex' sets keys codex does not understand:
  access  — did you mean 'sandbox'?
  understood: approvalPolicy, collaborationMode, effort, ephemeral, model, …
```

The engine would drop an unrecognised key silently. For `model` that costs you a model; for a
sandbox setting it means the profile reads as a restriction the CLI never receives — so the run
stops instead.

`extraArgs` is the escape hatch for whatever the mappers do not cover. It applies to the printed
resume command only; see `--launch` above.

### Permissions are not translated

`permissionMode` is Claude's; `approvalPolicy` and `sandbox` are Codex's; `mode` is Copilot's.
There is no honest mapping between them, so each profile states its own target's — and agentmove
prints them, marked with whether the start method you chose actually applies them:

```
  permissions: approvalPolicy=on-request  sandbox=read-only
```

That is the point of profiles rather than interactive flag entry: switching agents must not be how
an agent quietly gains more reach than it had, and a file you wrote last week is easier to review
than flags typed while something is broken.

Under `--launch` there is a second answer to the same problem: the CLI's permission questions are
asked *here*, in its own vocabulary, as they come up.

```
  ! wants to use: Write
    allow? [y]es / [n]o / [a]lways: n

declined
```

`[a]lways` applies for the rest of the session. With stdin not a terminal there is nobody to ask,
so requests are denied — the only answer that cannot widen what the agent may do.

One gap: a CLI question with structured options (Claude's AskUserQuestion, Codex's
`requestUserInput`) is shown and answered as free text. Claude's reply builder accepts that; Codex
wants a structured `answers` object, receives an empty one, and its agent generally re-asks in
prose.

## Non-interactive

```bash
agentmove --from claude --session e9b8e444 --to codex --yes
```

`--session` takes any unique prefix. With stdin not a terminal, agentmove refuses to guess and tells
you which flag was missing.

| Flag | |
|---|---|
| `--dir <path>` | directory to look in (default: current) |
| `--from <cli>` | `claude` \| `codex` \| `copilot` |
| `--session <id>` | unique prefix is enough |
| `--to <profile>` | profile name |
| `--limit <n>` | how many sessions to list (default 15) |
| `--config <path>` | config file |
| `--launch`, `-l` | start the target here instead of printing a command |
| `--yes` | skip the confirmation |
| `--init` | write a starter config |

## What it does to the conversation

- **An unfinished trailing turn is trimmed** — a turn the agent produced nothing for would
  otherwise reach the target twice, once as history and once as the thing to do.
- **A turn cut off mid-way is kept.** Four files edited out of five is work worth carrying; the
  handoff says the last step's outcome is unknown instead of throwing it away.
- **Long conversations can be summarised** (`summariseOver`) into a briefing, because every move
  re-ingests the whole transcript and can overflow the target's context.
- **Claude Code, Codex and GitHub Copilot CLI** are supported as both source and target. OpenCode
  has no store yet.

Conversion is lossy — opaque reasoning cannot cross, and tool calls with no equivalent become prose.
The handoff includes the path to the original transcript so anything missing can still be read.
