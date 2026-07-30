#!/usr/bin/env bash
# mintokei-broker-creds-sync — the nested-Docker counterpart of the Kubernetes broker's refresher sidecar
# (see KubernetesBrokerSpec, which runs the equivalent loop inside the broker Pod).
#
# Install on a runner host that provisions brokered sandboxes, as a Type=simple systemd service:
#
#     cp scripts/sandbox/broker-creds-sync.sh /usr/local/bin/mintokei-broker-creds-sync.sh
#     systemctl enable --now mintokei-broker-creds-sync
#
# Env: MINTOKEI_CLAUDE_CREDS (source token), MINTOKEI_SEED_ROOT (staging root — must match
# Sandbox:SeedStagingRoot), MINTOKEI_SYNC_INTERVAL_SECONDS. `--once` runs a single pass, for testing.
#
# Keeps each NESTED-Docker broker's STAGED .credentials.json in sync with the live host token, so the broker
# (which now resolves the ${json:} token ref PER-REQUEST) injects the CURRENT token instead of the point-in-time
# snapshot staged at provision. Without this, a host-token rotation mid-session leaves the broker injecting a
# stale/revoked token → sandbox "401 OAuth token expired/revoked". (The K8s path does the equivalent via a
# refresher sidecar in the broker Pod.)
#
# Writes ATOMICALLY (temp + mv) so the broker's per-request read never sees a partial file, and preserves each
# staged file's existing owner (the broker uid the stager chose). Runs as a loop (systemd Type=simple).
set -uo pipefail

SRC="${MINTOKEI_CLAUDE_CREDS:-/root/.claude/.credentials.json}"
SEED_ROOT="${MINTOKEI_SEED_ROOT:-/tmp/mintokei-sandbox-seed}"
INTERVAL="${MINTOKEI_SYNC_INTERVAL_SECONDS:-15}"

# Names of the sandbox/broker containers that currently exist (running OR exited). Empty on any docker
# failure, which is treated as "unknown" below — an unreachable daemon must not make every copy look orphaned.
live_containers() {
    docker ps -a --filter "label=mintokei.sandbox=1" --format '{{.Names}}' 2>/dev/null
}

sync_once() {
    [[ -f "$SRC" ]] || return 0
    shopt -s nullglob

    # Refresh ONLY copies whose session still exists. Without this check the sync keeps an ORPHANED copy
    # permanently valid: a staged copy that outlived its session (teardown is best-effort, so this happens)
    # would otherwise be rewritten with the live token every interval, leaving a current credential at a
    # predictable path indefinitely instead of one that ages out. Observed in production — a copy survived its
    # session by five days and was still 4 seconds behind the host's own token rotation.
    #
    # Removal is the library's job (SandboxCredentialStager.SweepAsync, driven by reconcile). This only
    # declines to keep an orphan alive, so the two are independent: whichever runs, the orphan stops being
    # a valid credential.
    local live
    live="$(live_containers)"
    local docker_ok=$?

    for t in "$SEED_ROOT"/*/.claude/.credentials.json; do
        [[ -f "$t" ]] || continue

        # <root>/<session>/.claude/.credentials.json -> <session>
        local session
        session="$(basename "$(dirname "$(dirname "$t")")")"

        # Skip an orphan — but only when docker actually answered. If it did not, fall through and sync: a
        # transient daemon blip must not stall token refresh for live sessions.
        if (( docker_ok == 0 )) && ! grep -qxF "$session" <<<"$live"; then
            continue
        fi

        cmp -s "$SRC" "$t" && continue                    # already current — skip
        owner="$(stat -c '%u:%g' "$t" 2>/dev/null)" || continue
        tmp="$(dirname "$t")/.credentials.json.sync.$$"
        if cp -f "$SRC" "$tmp" 2>/dev/null && chown "$owner" "$tmp" 2>/dev/null; then
            mv -f "$tmp" "$t" 2>/dev/null || rm -f "$tmp" 2>/dev/null
        else
            rm -f "$tmp" 2>/dev/null
        fi
    done
}

if [[ "${1:-}" == "--once" ]]; then sync_once; exit 0; fi
while true; do sync_once; sleep "$INTERVAL"; done
