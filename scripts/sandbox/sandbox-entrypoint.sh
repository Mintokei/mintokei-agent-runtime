#!/usr/bin/env bash
# Sandbox container entrypoint. If a workspace repo is requested, provision it from
# the RO mirror BEFORE the runner connects (so the machine comes online with the
# source repo already checked out and Mintokei's worktree-add flow just works).
# All args are passed through to the runner (run-sandbox-spike.sh appends
# --backend/--token/--name after the image).
set -euo pipefail

# Seed writable agent-CLI credentials from a read-only /seed mount. The Sandbox Manager
# mounts host creds RO under /seed (per-tenant); Claude Code / Codex need a WRITABLE config
# dir (session + history files), so we copy rather than mount the live dir into place.
seed_creds() {
  [[ -d /seed ]] || return 0
  if [[ -f /seed/.claude/.credentials.json || -f /seed/.claude.json ]]; then
    mkdir -p "${HOME:-/root}/.claude"
    [[ -f /seed/.claude/.credentials.json ]] && cp /seed/.claude/.credentials.json "${HOME:-/root}/.claude/.credentials.json"
    [[ -f /seed/.claude.json ]] && cp /seed/.claude.json "${HOME:-/root}/.claude.json"
  fi
  [[ -d /seed/.codex ]] && { mkdir -p "${HOME:-/root}/.codex"; cp -a /seed/.codex/. "${HOME:-/root}/.codex/"; }
  return 0
}
seed_creds

if [[ -n "${SANDBOX_REPO_URL:-}" ]]; then
  prepare-workspace || { echo "sandbox-entrypoint: prepare-workspace failed" >&2; exit 1; }
fi

exec /opt/runner/Mintokei.Runner --data-dir /data "$@"
