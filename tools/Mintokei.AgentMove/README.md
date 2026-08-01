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

## Configuration

```bash
agentmove --init          # writes ./agentmove.json
```

Read from `--config`, then `./agentmove.json`, then `$XDG_CONFIG_HOME/agentmove/config.json`.
Without one, two conservative built-in profiles are used.

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

`config` goes straight to `AgentSessionSpec.Config`, which each backend's config mapper already
turns into that CLI's arguments — so a profile can express anything the engine can launch, and
picks up new keys as the mappers grow:

| Backend | Keys |
|---|---|
| claude | `model` `effort` `permissionMode` `allowedTools` `allowDangerouslySkipPermissions` |
| codex | `model` `effort` `approvalPolicy` `access` `collaborationMode` |
| copilot | `model` `effort` `autopilot` `allowAllPaths` `disableAskUser` `disableBuiltinMcps` |
| opencode | `model` `agent` `dangerouslySkipPermissions` |

`extraArgs` is the escape hatch for whatever the mappers do not cover.

### Permissions are not translated

`permissionMode` is Claude's; `approvalPolicy` and `access` are Codex's. There is no honest mapping
between them, so each profile states its own target's — and agentmove prints them before it does
anything:

```
  permissions: approvalPolicy=on-request
```

That is the point of profiles rather than interactive flag entry: switching agents must not be how
an agent quietly gains more reach than it had, and a file you wrote last week is easier to review
than flags typed while something is broken.

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
