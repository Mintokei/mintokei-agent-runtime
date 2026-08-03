# Fixtures

Real transcripts, captured from each CLI and then scrubbed. Real ones rather than hand-written
because every store here exists to read a format nobody documents — a fixture invented from the
parser proves only that the parser agrees with itself.

| | |
|---|---|
| `claude-real-transcript.jsonl` | Claude Code, `~/.claude/projects/<slug>/<id>.jsonl` |
| `codex-real-rollout.jsonl` | Codex, `~/.codex/sessions/<y>/<m>/<d>/rollout-*.jsonl` |
| `copilot-real-events.jsonl` | GitHub Copilot CLI, `~/.copilot/session-state/<id>/events.jsonl` |

## Before adding one

**Read the system preamble, not just the conversation.** That is where host information hides, and
it is the part you will skim past because it is boilerplate.

This bit us once. The conversation in `codex-real-rollout.jsonl` was synthetic throughout — a made
up file with a made up value in it — and the fixture still shipped a description of a production
deployment: image names, a Kubernetes namespace, a service name, the preferred deploy method. None
of it was typed by anyone. Codex injects the host's installed skills and connectors into a
`developer` turn at the head of every session, so capturing a rollout captures whatever that
machine had configured. The repo is public.

Both blocks now carry a placeholder saying what they were. What to check in a new capture:

- **Codex** — `<skills_instructions>` and `<apps_instructions>` in the leading `developer` turn,
  plus any `<recommended_plugins>` list. Every one names something about the host.
- **Claude Code** — the `system` line's tool list, and `cwd` / `gitBranch` on every line.
- **Copilot** — `workspace.yaml` and the `session.start` event.
- **All three** — absolute paths. These were rewritten to `/tmp/fixture-home`; keep that.

Then grep for the obvious — keys, tokens, hostnames, internal service names — and read the
preamble anyway, because the obvious things are not what got through last time.

## Keeping them honest

Scrub the preamble, not the conversation. The tests assert on line kinds, message ordering, tool
call pairing and id derivation; none of them read skill text, so a placeholder costs nothing. What
must stay real is the *shape* — the envelope keys, the field names, the ordering each CLI actually
writes. That is the whole reason these files exist.
