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

## Or go straight into the CLI: `--attach`

`--attach` runs the resume command for you, handing this terminal to the target CLI's own
interface — the real TUI, with its colours, keybindings and slash commands — and passes the handoff
along as the session's opening turn, shown first so nothing is sent on your behalf unseen:

```
  Claude Code  ->  codex (codex/gpt-5.5)
  start:       codex's own interface, in this terminal
  permissions: approvalPolicy=on-request  sandbox=read-only

  moved 4 message(s) as 019fc2a8-5d4e-7733-8540-bc0b6f375c21

  sending as the first turn (--no-handoff to skip):

    [handoff] This conversation was moved here from Claude Code.
    …

  starting codex…
```

With `--no-handoff` the session simply opens, history and all, and you type the first message
yourself:

```
  starting codex — no opening turn, the history is already there
```

which is this, spawned as a child of agentmove with your stdin, stdout and stderr:

```
codex resume 019fc2a8-… --ask-for-approval on-request --sandbox read-only \
  --config model_reasoning_effort=low --model gpt-5.5 "[handoff] This conversation was moved…"
```

The profile survives the crossing. Every CLI turns out to have flags for what a profile can say —
Claude `--permission-mode` / `--effort`, Codex `--sandbox` / `--ask-for-approval` plus `-c` for its
`config.toml` fields, Copilot `--mode` / `--allow-all-paths` — so `--attach` is not the lossy
option it looks like.

agentmove sees nothing from the moment the TUI starts — it paints escape sequences meant for a
human's eyes, not events for a program. `--attach` is an `exec` with a transcript conversion in
front of it, which is the whole job. Driving a CLI over its protocol and reacting to what it says
is a different tool; `samples/FailoverAgentMinimal` is that one.

It refuses rather than start an agent with permissions the profile did not ask for — before writing
anything, so no half-moved session is left behind:

```
--attach cannot apply dangerouslySkipPermissions: opencode has no flag for it, so the agent would
run with its own defaults instead of what this profile says.
```

### Which of the two

|  | default | `--attach` |
|---|---|---|
| who starts the CLI | you | agentmove |
| what you see | a command to copy | the real TUI |
| profile applied | yes, if you run it as printed | yes |
| opening turn | printed to paste | sent, shown first |

The default is for when you want to see the command before running it, or run it somewhere else.
`--attach` is for "I moved it, now let me work".

Leaving the CLI does not end anything. The session is a normal session in the target's own store, so
`codex resume <id>` picks it up again whenever you want.

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
  "summariseOver": 400,

  // The opening turn. Omit for the built-in wording; "" to send nothing.
  "handoff": "You were interrupted. Continue: {request}"
}
```

Placeholders: `{request}` `{reason}` `{failureKind}` `{sourceCli}` `{targetCli}` `{sourceSessionId}`
`{sourcePath}` `{cwd}` `{unresolvedToolCall}`. A line whose placeholder has no value is dropped
whole, so keep the label on the **same line** as its placeholder — `Outstanding request: {request}`
— or a label survives when its value does not and you get a heading with nothing under it.

For one run: `--handoff <text>` (also `default` or `minimal`), `--handoff-file <path>`, or
`--no-handoff`. The flag beats the config file; `--no-handoff` beats both.

`config` is the engine's own vocabulary; `CliArgs` turns it into that CLI's own flags, so a profile
means the same thing whether the command is printed or run:

| Backend | Keys |
|---|---|
| claude | `model` `effort` `maxTurns` `permissionMode` `allowedTools` `systemPromptFile` `allowDangerouslySkipPermissions` `verbose` |
| codex | `model` `modelProvider` `modelVerbosity` `effort` `summary` `personality` `approvalPolicy` `sandbox` `webSearch` `noProjectDoc` |
| copilot | `model` `effort` `mode` `allowAllPaths` `disableAskUser` `disableBuiltinMcps` `enableAllGithubMcpTools` `maxAutopilotContinues` |
| opencode | `model` `agent` `dangerouslySkipPermissions` |

`ephemeral` and `collaborationMode` are deliberately absent. The first only affects *creating* a
thread and agentmove always resumes one; the second Codex takes only over its app-server protocol,
which agentmove does not speak — it starts the CLI's own interface instead. Both are refused with
the reason rather than accepted, mapped, sent nowhere and never mentioned.

A key outside its backend's list is an **error**, not a shrug:

```
profile 'codex' sets keys codex does not understand:
  access  — did you mean 'sandbox'?
  understood: approvalPolicy, collaborationMode, effort, ephemeral, model, …
```

The engine would drop an unrecognised key silently. For `model` that costs you a model; for a
sandbox setting it means the profile reads as a restriction the CLI never receives — so the run
stops instead.

`extraArgs` is the escape hatch for whatever the mappers do not cover. Verbatim and unvalidated —
the CLI is the one that judges it. Prefer a `config` key when one exists: those are checked against
the backend before anything is written.

Codex's command-line form was checked against the installed CLI rather than assumed: `--sandbox`
and `--ask-for-approval` take the same values the config keys do, and the rest go through
`-c <config.toml field>=<value>`, whose names were verified with `codex exec --strict-config`,
which rejects an unknown field instead of ignoring it.

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

Once the CLI is running it asks its own permission questions, in its own interface. agentmove is
not in the middle of that and does not try to be — the profile decides what the session starts
with, and the CLI decides everything after.

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
| `--attach`, `-a` | hand this terminal to the target CLI's own interface |
| `--handoff <text>` | opening turn: `default`, `minimal`, or a literal template |
| `--handoff-file <p>` | read that template from a file |
| `--no-handoff` | send no opening turn at all |
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
