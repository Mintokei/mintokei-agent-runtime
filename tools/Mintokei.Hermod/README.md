# hermod

Pick a session from one agent CLI and carry on with it in another.

```bash
dotnet tool install -g Mintokei.Hermod
```

`Mintokei.Hermod` is the package; `hermod` is the command. It needs the .NET runtime and at
least one agent CLI already installed — it reads and writes their own session stores rather than
keeping any of its own.

**Claude Code, Codex and GitHub Copilot CLI**, as both source and target, in any of the six
directions. OpenCode has no transcript store yet, and Gemini CLI is not supported.

**No configuration needed to start.** There is a built-in profile per CLI, each one conservative
about permissions. Reach for [`hermod --init`](#configuration) when you want to pin a model, or
say what the target may do once it is running.

> Hermóðr rode nine nights down to Hel to bring Baldr back. Retrieving something from the place
> things do not come back from is the job, so it seemed fair to borrow the name.

Run it in the directory the work happened in. It lists what each CLI recorded there — by
description, not by id — and moves the one you choose into whichever agent you want to continue in.

```
$ hermod

directory  /repo
config     ./hermod.json

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

which is this, spawned as a child of hermod with your stdin, stdout and stderr:

```
codex resume 019fc2a8-… --ask-for-approval on-request --sandbox read-only \
  --config model_reasoning_effort=low --model gpt-5.5 "[handoff] This conversation was moved…"
```

The profile survives the crossing. Every CLI turns out to have flags for what a profile can say —
Claude `--permission-mode` / `--effort`, Codex `--sandbox` / `--ask-for-approval` plus `-c` for its
`config.toml` fields, Copilot `--mode` / `--allow-all-paths` — so `--attach` is not the lossy
option it looks like.

hermod sees nothing from the moment the TUI starts — it paints escape sequences meant for a
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
| who starts the CLI | you | hermod |
| what you see | a command to copy | the real TUI |
| profile applied | yes, if you run it as printed | yes |
| opening turn | printed to paste | sent, shown first |

The default is for when you want to see the command before running it, or run it somewhere else.
`--attach` is for "I moved it, now let me work".

Leaving the CLI does not end anything. The session is a normal session in the target's own store, so
`codex resume <id>` picks it up again whenever you want.

## Configuration

```bash
hermod --init          # writes ./hermod.json
```

Read from `--config`, then `./hermod.json`, then `$XDG_CONFIG_HOME/hermod/config.json`.
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
  "summary": { "when": 400, "with": "mechanical" },

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

## Summarising

Off by default. Moving the real transcript is the point of the tool; a briefing is what you reach
for when the conversation will not fit — every move re-ingests the whole thing, and the cost is paid
again on every hop.

Two independent choices — **when** it happens, and **who writes it**:

```json
"summary": {
  "when": "always",           // "always" | "never" | <message count>
  "with": "claude-fast",      // "mechanical" | any profile name
  "prompt": "Read {sourcePath}. …",
  "keepFacts": true
}
```

|  | `"when": "never"` | `"when": 400` | `"when": "always"` |
|---|---|---|---|
| `"with": "mechanical"` | move the transcript | summarise past 400 messages | summarise every session |
| `"with": "<profile>"` | move the transcript | that agent writes it, past 400 | that agent writes it, always |

`when` is one field rather than a mode plus a threshold, because `{"when": "always", "over": 400}`
is expressible and meaningless — and then the parser has to have an opinion about it.

For one run: `--summarise` (always), `--summarise 400`, `--no-summarise`, `--summarise-with <who>`,
`--summary-prompt <text>`, `--summary-prompt-file <path>`. Flag beats the config file, the `--no-`
form beats both — the same rule as `--handoff`, so there is one to learn rather than two.

### `mechanical` — extracted

Free, instant, deterministic, and about as insightful as `git log --stat`. It reads the transcript
and lists what is in it: the requests in order, the files touched, the recent commands, and the
previous agent's closing message verbatim.

### A profile — an agent reads it and writes the handover

```json
"summary": { "when": "always", "with": "claude-reader" }
```

It is handed the **path**, never the text. Pasting a conversation into a prompt would hit the same
context limit that made summarising worth doing; a path lets the agent read as much as it needs with
its own tools. It reads the **source** transcript rather than the converted one, so the briefing can
carry across what conversion drops — opaque reasoning, tool calls the target has no form for.

The briefing is **added to** the extracted facts, not swapped for them. A model can be wrong about
what it read; the file list cannot be. `"keepFacts": false` if you want only the prose.

What it produces, on a real session, next to what extraction produced from the same file:

```
## Files touched                      ← mechanical: what the transcript records
- /root/live/web.yaml
- /root/live/client.yaml

## Handover briefing (written by an agent that read the transcript)

**No Edit tool call is recorded anywhere in the prior session.** The three lines reading
"The file /root/live/web.yaml has been updated successfully" are recorded as assistant
message content with an empty toolRequests list. They are text the model produced, not
tool results. …File mtimes are all before the recorded session start.
```

That is the difference worth paying a model call for: the previous agent had claimed edits it never
made, and only something that *read* the transcript could notice.

**It is a second agent, started in your working directory.** hermod says so before it asks you to
proceed, along with whether the profile pins it to read-only:

```
  summary:     every session, written by 'claude-fast'
               claude runs here to read the transcript, as claude-sonnet-4-5
               this profile does not pin it to read-only, so it may change files here
```

Every permission request it makes is refused. That covers what a CLI stops to ask about — but a
profile saying `acceptEdits` or `sandbox: workspace-write` has already been granted the reach and is
never asked, which is why the line above exists. Silence in a profile is not read-only: it means the
CLI's own default, which differs per tool and per version.

Because the stores live outside the working directory (`~/.claude`, `~/.codex`, `~/.copilot`) and
every CLI asks before reading there, the transcript is copied into the working directory for the run
and deleted afterwards. Without that the summariser is denied its only source and produces nothing.

If it fails — timeout, no such CLI, nothing said — the move continues with the extracted briefing
and says why. You asked for a move; the summary is a means.

`config` is the engine's own vocabulary; `CliArgs` turns it into that CLI's own flags, so a profile
means the same thing whether the command is printed or run:

| Backend | Keys |
|---|---|
| claude | `model` `effort` `maxTurns` `permissionMode` `allowedTools` `systemPromptFile` `allowDangerouslySkipPermissions` `verbose` |
| codex | `model` `modelProvider` `modelVerbosity` `effort` `summary` `personality` `approvalPolicy` `sandbox` `webSearch` `noProjectDoc` |
| copilot | `model` `effort` `mode` `allowAllPaths` `disableAskUser` `disableBuiltinMcps` `enableAllGithubMcpTools` `maxAutopilotContinues` |
| opencode | `model` `agent` `dangerouslySkipPermissions` |

`ephemeral` and `collaborationMode` are deliberately absent. The first only affects *creating* a
thread and hermod always resumes one; the second Codex takes only over its app-server protocol,
which hermod does not speak — it starts the CLI's own interface instead. Both are refused with
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
There is no honest mapping between them, so each profile states its own target's — and hermod
prints them, marked with whether the start method you chose actually applies them:

```
  permissions: approvalPolicy=on-request  sandbox=read-only
```

That is the point of profiles rather than interactive flag entry: switching agents must not be how
an agent quietly gains more reach than it had, and a file you wrote last week is easier to review
than flags typed while something is broken.

Once the CLI is running it asks its own permission questions, in its own interface. hermod is
not in the middle of that and does not try to be — the profile decides what the session starts
with, and the CLI decides everything after.

## Non-interactive

```bash
hermod --from claude --session e9b8e444 --to codex --yes
```

`--session` takes any unique prefix. With stdin not a terminal, hermod refuses to guess and tells
you which flag was missing.

## All flags

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
| `--summarise [n]` | briefing instead of the transcript — always, or past n messages |
| `--no-summarise` | move the real transcript whatever the config says |
| `--summarise-with <who>` | `mechanical`, or a profile name to write it |
| `--summary-prompt <text>` | what to ask that profile for |
| `--summary-prompt-file <p>` | read that prompt from a file |
| `--yes` | skip the confirmation |
| `--init` | write a starter config |

## What it does to the conversation

- **An unfinished trailing turn is trimmed** — a turn the agent produced nothing for would
  otherwise reach the target twice, once as history and once as the thing to do.
- **A turn cut off mid-way is kept.** Four files edited out of five is work worth carrying; the
  handoff says the last step's outcome is unknown instead of throwing it away.
- **A turn the provider refused is dropped.** Every CLI files a rate limit or an API error as an
  ordinary assistant message with a flag beside it, so carried over verbatim it reads as something
  the agent said — `You've hit your session limit` lands in the new session as a sentence *it*
  supposedly wrote. What follows the failure is real work and is kept.
- **Conversations can be summarised** into a briefing — see [Summarising](#summarising) — because
  every move re-ingests the whole transcript and can overflow the target's context. Off unless you
  ask for it.
- **Claude Code, Codex and GitHub Copilot CLI** are supported as both source and target. OpenCode
  has no store yet.

Conversion is lossy — opaque reasoning cannot cross, and tool calls with no equivalent become prose.
The handoff includes the path to the original transcript so anything missing can still be read.

## When it does not work

**`hermod: command not found`** — `dotnet tool install -g` puts it in `~/.dotnet/tools`, which is
not always on `PATH`. The installer prints the fix as a single line that scrolls past:

```bash
export PATH="$PATH:$HOME/.dotnet/tools"     # add to ~/.bashrc or ~/.zshrc
```

On Windows the equivalent is `%USERPROFILE%\.dotnet\tools`.

**`No sessions found for <dir>`** — almost always the directory, not the sessions. Every CLI here
records which directory a conversation happened in, and hermod only lists the ones belonging to
where you are standing. `cd` to the repository you were working in, or point at it:

```bash
hermod --dir ~/projects/the-one-you-meant
```

A session started somewhere else is invisible here on purpose. Listing every session on the machine
would mean picking a conversation about a different codebase and resuming it against this one.

**Only one CLI is listed when you expected two** — hermod lists a CLI only when it has sessions in
this directory. An installed CLI you have never used here does not appear.

**The target CLI does not show your moved session in its own picker** — the transcript is written
and `<cli> resume <id>` works regardless; what may be missing is the index row that makes it
*discoverable*. hermod writes one where the CLI keeps an index, but treats a locked or migrated
database as non-fatal rather than failing a move that already succeeded. The id it printed is
always enough.
