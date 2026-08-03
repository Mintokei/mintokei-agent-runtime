#!/usr/bin/env bash
#
# Move real conversations between the installed agent CLIs and check the target draws the right
# conclusion from what arrived.
#
# The unit tests prove the DATA survives a crossing. They cannot prove an agent reads it correctly:
# a file edit crosses as assistant prose rather than a tool result, and whether that stops the next
# agent redoing the work is a question only a real agent answers. Nor can they see the CLIs
# themselves change — three of the bugs this suite exists for were CLI-shaped, not code-shaped
# (`--no-project-doc` was not a flag Codex had; `access` and `autopilot` were not keys it wanted).
#
# So: run this after a CLI updates, not on every commit. It spends real tokens.
#
#   scripts/live-check.sh              every case, every installed target
#   scripts/live-check.sh t6           just that one
#   KEEP=1 scripts/live-check.sh       leave the scratch directories for inspection
#
#   t0  a move into the CLI it came from
#   t1  a file edit, which crosses as prose rather than a tool result
#   t2  a failed command, whose exit status no format has a field for
#   t4  an MCP call moved into a CLI with no such server
#   t5  a finding a sub-agent produced
#   t6  a run interrupted part-way, finished on another CLI
#   t8  unicode, code fences and a 600 KB tool result
#
# Not covered here: --attach, which needs a terminal to answer the CLI's startup handshake, and a
# conversation long enough to trigger auto-compaction.
#
# Each case plants a value that exists nowhere the target can read, moves the session, then asks
# for it back with "without reading any files". A correct answer can only come from carried history.

set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
AGENTMOVE=(dotnet run --project "$REPO/tools/Mintokei.AgentMove" --)
WORK="${WORK:-$(mktemp -d /tmp/live-check.XXXXXX)}"
PASS=0 FAIL=0 SKIP=0

cleanup() { [[ -n "${KEEP:-}" ]] || rm -rf "$WORK"; }
trap cleanup EXIT

say()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
pass() { printf '  \033[32mPASS\033[0m  %s\n' "$*"; PASS=$((PASS + 1)); }
fail() { printf '  \033[31mFAIL\033[0m  %s\n' "$*"; FAIL=$((FAIL + 1)); }
skip() { printf '  \033[33mSKIP\033[0m  %s\n' "$*"; SKIP=$((SKIP + 1)); }

have() { command -v "$1" >/dev/null 2>&1; }

# Targets are whatever is installed. A missing CLI skips rather than fails: this is a check on the
# ones you have, not a demand that you have all of them.
TARGETS=()
have codex && TARGETS+=(codex)
have copilot && TARGETS+=(copilot)

profiles() {
    cat > "$1/agentmove.json" <<'JSON'
{
  "profiles": {
    "codex":   { "tool": "codex",   "config": { "sandbox": "read-only", "approvalPolicy": "on-request" } },
    "copilot": { "tool": "copilot", "config": { "mode": "interactive" } },
    "claude":  { "tool": "claude",  "config": { "permissionMode": "default" } }
  }
}
JSON
}

# The id of the newest Claude session for a directory.
newest_claude_session() {
    local slug="${1//\//-}"
    local dir="$HOME/.claude/projects/$slug"
    [[ -d $dir ]] || return 1
    basename "$(ls -t "$dir"/*.jsonl 2>/dev/null | head -1)" .jsonl
}

# Moves the session and echoes the new id, or nothing. Its full output goes to $MOVE_LOG so a
# failure can be read afterwards rather than guessed at.
#
# </dev/null on every CLI call is not decoration: both `dotnet run` and the agent CLIs read stdin,
# and inside a loop they will eat the script's own, which shows up as an unrelated command hanging
# or returning nothing.
move() {
    local dir=$1 source=$2 target=$3
    MOVE_LOG="$WORK/move-$target.log"
    (cd "$dir" && "${AGENTMOVE[@]}" --from claude --session "$source" --to "$target" \
        --yes --no-handoff </dev/null >"$MOVE_LOG" 2>&1)
    grep -oE 'as [0-9a-f-]{36}' "$MOVE_LOG" | cut -d' ' -f2
}

# Asks the target a question in the moved session and echoes its reply.
ask() {
    local dir=$1 target=$2 id=$3 question=$4
    case $target in
        codex)
            (cd "$dir" && timeout 300 codex exec --skip-git-repo-check --sandbox read-only \
                resume "$id" "$question" </dev/null 2>&1) | grep -vE '^(warning|tokens used|[0-9,]+)$' ;;
        copilot)
            (cd "$dir" && timeout 300 copilot --resume "$id" -p "$question" --allow-all-tools </dev/null 2>&1) \
                | grep -vE '^(Changes|AI Credits|Tokens|Resume) ' ;;
    esac
}

# Runs one case against every installed target: move, ask, look for `expect` in the reply.
check() {
    local dir=$1 source=$2 label=$3 question=$4 expect=$5
    for target in "${TARGETS[@]}"; do
        local id reply
        id=$(move "$dir" "$source" "$target")
        if [[ -z $id ]]; then
            fail "$label -> $target (the move produced no session)"
            sed 's/^/          /' <<<"$(tail -4 "$MOVE_LOG")"
            continue
        fi
        reply=$(ask "$dir" "$target" "$id" "$question")
        if grep -qiF -- "$expect" <<<"$reply"; then
            pass "$label -> $target"
        else
            fail "$label -> $target (wanted '$expect')"
            sed 's/^/          /' <<<"$(tail -6 <<<"$reply")"
        fi
    done
}

# ── t0: a move into the CLI it came from ────────────────────────────────────────────────────────
#
# The cheap path — same store, no conversion — and the one where a mistake would be quietest: a new
# session that overwrote the old one, or reused its id, would look like success until the original
# was wanted again.
t0() {
    say "t0  same-CLI moves"
    local dir="$WORK/t0"
    mkdir -p "$dir" && profiles "$dir"
    printf 'seal: HERON-88
' > "$dir/data.txt"

    (cd "$dir" && timeout 300 claude -p "Read data.txt and tell me the seal value." \
        --allowed-tools Read --permission-mode acceptEdits </dev/null >/dev/null 2>&1)

    local source; source=$(newest_claude_session "$dir") || { skip "t0 (claude recorded no session)"; return; }

    local id; id=$(move "$dir" "$source" claude)
    if [[ -z $id ]]; then
        fail "same-CLI -> claude (the move produced no session)"
    elif [[ $id == "$source" ]]; then
        fail "same-CLI -> claude (reused the source id $id — the original may be overwritten)"
    elif [[ ! -f "$HOME/.claude/projects/${dir//\//-}/$source.jsonl" ]]; then
        fail "same-CLI -> claude (the source session is gone)"
    else
        pass "same-CLI -> claude (new id, original intact)"
    fi
}

# ── t1: a file edit crosses as prose. Does the target know what changed? ─────────────────────────
#
# The case the narration fix was written for. An edit with no narration used to be dropped whole,
# so the moved conversation showed the request and no answer — which reads as work never done.
t1() {
    say "t1  file edits"
    local dir="$WORK/t1"
    mkdir -p "$dir" && profiles "$dir"
    printf 'port: 8080\n' > "$dir/web.yaml"
    printf 'retries: 3\n' > "$dir/client.yaml"

    (cd "$dir" && timeout 300 claude -p \
        "Set port to 9090 in web.yaml and retries to 5 in client.yaml. Use the Edit tool for each." \
        --permission-mode acceptEdits </dev/null >/dev/null 2>&1)

    local source; source=$(newest_claude_session "$dir") || { skip "t1 (claude recorded no session)"; return; }
    check "$dir" "$source" "edits" \
        "Without reading any files: list each file you edited earlier in this conversation and the value you set." \
        "9090"
}

# ── t2: a failed command. Does the exit status survive? ──────────────────────────────────────────
#
# No format has an exit-code field; all three recover a number by matching what a shell tool prints,
# and for a while no writer printed one. A failure that crosses as prose alone is one the next agent
# reads as a success.
t2() {
    say "t2  a failed command"
    local dir="$WORK/t2"
    mkdir -p "$dir" && profiles "$dir"
    printf '#!/bin/sh\necho "Failed! - Failed: 3, Passed: 12"\nexit 1\n' > "$dir/runtests.sh"
    chmod +x "$dir/runtests.sh"

    (cd "$dir" && timeout 300 claude -p "Run ./runtests.sh and say in one sentence whether the tests passed." \
        --allowed-tools Bash --permission-mode acceptEdits </dev/null >/dev/null 2>&1)

    local source; source=$(newest_claude_session "$dir") || { skip "t2 (claude recorded no session)"; return; }
    check "$dir" "$source" "exit status" \
        "Without running anything: did the test run earlier in this conversation pass or fail, and what was its exit status?" \
        "1"
}

# ── t4: an MCP call into a CLI that has no such server ───────────────────────────────────────────
#
# The one whose outcome was not predictable. The server name is written into the tool name as
# mcp__<server>__<tool>; without it the call arrives looking like a native tool the target simply
# does not have, and it cannot tell why. With it, the target reports the tool as unavailable rather
# than inventing a result.
t4() {
    say "t4  an mcp call the target cannot repeat"
    local dir="$WORK/t4"
    mkdir -p "$dir" && profiles "$dir"
    cp "$REPO/scripts/live-check-mcp.py" "$dir/ledger_mcp.py"

    claude mcp add live-check-ledger -s local -- python3 "$dir/ledger_mcp.py" >/dev/null 2>&1
    (cd "$dir" && timeout 300 claude -p "Use the live-check-ledger MCP tool to look up the badge for employee 4821, then state the badge." \
        --allowed-tools "mcp__live-check-ledger__lookup_badge" --permission-mode acceptEdits </dev/null >/dev/null 2>&1)
    claude mcp remove live-check-ledger -s local >/dev/null 2>&1

    local source; source=$(newest_claude_session "$dir") || { skip "t4 (claude recorded no session)"; return; }
    check "$dir" "$source" "mcp result carried" \
        "Without reading any files: what badge did you find for employee 4821 earlier in this conversation?" \
        "FALCON-13"
}

# ── t8/t9: encoding, and a tool result too big to be casually handled ────────────────────────────
t8() {
    say "t8  unicode, code fences and a 600 KB tool result"
    local dir="$WORK/t8"
    mkdir -p "$dir" && profiles "$dir"
    printf '# Résumé — 「テスト」 🎯\n\nbadge: WREN-51\n' > "$dir/notes.md"
    python3 -c "
import sys
with open(sys.argv[1], 'w') as f:
    for i in range(20000): f.write(f'line {i}: the quick brown fox\n')" "$dir/big.log"

    (cd "$dir" && timeout 300 claude -p \
        "Read notes.md and big.log, then reply with exactly two lines: the first line of notes.md verbatim, then the last line of big.log verbatim." \
        --allowed-tools "Read,Bash" --permission-mode acceptEdits </dev/null >/dev/null 2>&1)

    local source; source=$(newest_claude_session "$dir") || { skip "t8 (claude recorded no session)"; return; }
    check "$dir" "$source" "unicode" \
        "Without reading any files: reply with exactly the two lines you reported earlier, verbatim." \
        "Résumé — 「テスト」 🎯"
    check "$dir" "$source" "large result" \
        "Without reading any files: what was the last line of big.log that you reported?" \
        "line 19999"
}

# ── t5: work done by a sub-agent ────────────────────────────────────────────────────────────────
#
# A Claude Task runs its own nested conversation and reports back. SubAgentExecution has no wire
# form anywhere, so it crosses as prose — the question is whether the finding survives that.
t5() {
    say "t5  a sub-agent's finding"
    local dir="$WORK/t5"
    mkdir -p "$dir" && profiles "$dir"
    printf 'The archive token is MERLIN-4.\n' > "$dir/report.txt"

    (cd "$dir" && timeout 300 claude -p \
        "Use the Task tool to launch a sub-agent that reads report.txt and reports the archive token. Then state the token." \
        --allowed-tools "Task,Read" --permission-mode acceptEdits </dev/null >/dev/null 2>&1)

    local source; source=$(newest_claude_session "$dir") || { skip "t5 (claude recorded no session)"; return; }
    check "$dir" "$source" "sub-agent finding" \
        "Without reading any files: what archive token was found earlier in this conversation?" \
        "MERLIN-4"
}

# ── t6: interrupted part-way, finished somewhere else ───────────────────────────────────────────
#
# The premise of the whole thing, and the case with the most moving parts: the trim has to keep the
# work already done, EndsMidTurn has to notice the last step has no result, and the handoff has to
# say so in a way that makes the next agent look before repeating.
#
# A marker is appended to the files that were finished. If the target redoes them rather than
# checking, the marker goes with it — which is the difference between "continued the work" and
# "started it again".
t6() {
    say "t6  interrupted mid-edit, finished elsewhere"
    local dir="$WORK/t6"
    mkdir -p "$dir" && profiles "$dir"
    local n
    for n in 1 2 3 4 5; do printf "value: $n\n" > "$dir/f$n.yaml"; done

    (cd "$dir" && timeout 300 claude -p \
        "Edit each of f1.yaml through f5.yaml in order, one Edit call each, setting value to 99. Work slowly and deliberately." \
        --permission-mode acceptEdits </dev/null >/dev/null 2>&1) &
    local pid=$!

    # Cut it off once some but not all of the work is done.
    local waited=0
    while [[ $(grep -lc 'value: 99' "$dir"/f*.yaml 2>/dev/null | wc -l) -lt 3 && $waited -lt 240 ]]; do
        sleep 2; waited=$((waited + 2))
    done
    sleep 1; kill -TERM $pid 2>/dev/null; wait $pid 2>/dev/null

    local done_count; done_count=$(grep -l 'value: 99' "$dir"/f*.yaml 2>/dev/null | wc -l)
    if [[ $done_count -eq 0 || $done_count -eq 5 ]]; then
        skip "t6 (wanted a partial edit, got $done_count of 5 — timing)"
        return
    fi
    # Anything the target overwrites rather than checks loses this.
    grep -l 'value: 99' "$dir"/f*.yaml | xargs -r sed -i 's/$/  # checked/'

    local source; source=$(newest_claude_session "$dir") || { skip "t6 (claude recorded no session)"; return; }
    printf '  interrupted after %s of 5 files\n' "$done_count"

    for target in "${TARGETS[@]}"; do
        local id reply
        id=$(move "$dir" "$source" "$target")
        [[ -n $id ]] || { fail "finish the job -> $target (the move produced no session)"; continue; }

        reply=$(ask "$dir" "$target" "$id" \
            "Check the current state of the workspace before repeating anything — earlier steps may already have taken effect. Then finish the outstanding request and say what you found and what you changed.")

        local finished markers
        finished=$(grep -lc 'value: 99' "$dir"/f*.yaml 2>/dev/null | wc -l)
        markers=$(grep -lc '# checked' "$dir"/f*.yaml 2>/dev/null | wc -l)

        if [[ $finished -ne 5 ]]; then
            fail "finish the job -> $target (only $finished of 5 files done)"
            sed 's/^/          /' <<<"$(tail -4 <<<"$reply")"
        elif [[ $markers -lt $done_count ]]; then
            fail "finish the job -> $target (redid $((done_count - markers)) file(s) instead of checking)"
        else
            pass "finish the job -> $target (finished $((5 - done_count)), left $done_count alone)"
        fi

        # Put it back for the next target.
        for n in 1 2 3 4 5; do
            if [[ $n -le $done_count ]]; then printf "value: 99  # checked\n" > "$dir/f$n.yaml"
            else printf "value: $n\n" > "$dir/f$n.yaml"; fi
        done
    done
}

# ── run ─────────────────────────────────────────────────────────────────────────────────────────

have claude || { echo "claude is not installed; every case starts from a Claude session." >&2; exit 1; }
if [[ ${#TARGETS[@]} -eq 0 ]]; then
    echo "no target CLI installed (codex, copilot); nothing to move into." >&2
    exit 1
fi

echo "targets:   ${TARGETS[*]}"
echo "scratch:   $WORK"
echo "note:      this spends real tokens and takes several minutes."

for case_name in "${@:-t0 t1 t2 t4 t5 t6 t8}"; do
    case $case_name in
        t0) t0 ;;
        t1) t1 ;;
        t2) t2 ;;
        t4) t4 ;;
        t5) t5 ;;
        t6) t6 ;;
        t8 | t9) t8 ;;
        *) echo "unknown case '$case_name' (t0, t1, t2, t4, t5, t6, t8)" >&2; exit 2 ;;
    esac
done

say "$PASS passed, $FAIL failed, $SKIP skipped"
[[ $FAIL -eq 0 ]]
