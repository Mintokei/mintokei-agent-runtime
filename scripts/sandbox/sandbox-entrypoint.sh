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

  # Git credentials for cloning a private repo over the network (GitCredentialsHostDir mounted at
  # /seed/git). Supports an HTTPS token store (.git-credentials) and/or an SSH key dir (.ssh/).
  if [[ -d /seed/git ]]; then
    if [[ -f /seed/git/.git-credentials ]]; then
      cp /seed/git/.git-credentials "${HOME:-/root}/.git-credentials"
      chmod 600 "${HOME:-/root}/.git-credentials" 2>/dev/null || true
      git config --global credential.helper store 2>/dev/null || true
    fi
    if [[ -d /seed/git/.ssh ]]; then
      mkdir -p "${HOME:-/root}/.ssh"
      cp -a /seed/git/.ssh/. "${HOME:-/root}/.ssh/"
      chmod 700 "${HOME:-/root}/.ssh" 2>/dev/null || true
      chmod 600 "${HOME:-/root}"/.ssh/* 2>/dev/null || true
    fi
  fi
  return 0
}
seed_creds

# Broker egress (SandboxEgress.Broker): point git at the per-session broker's credential mint so tokens are
# fetched on demand and NEVER written to disk. MINTOKEI_BROKER_CRED_URL is set by the runtime in broker mode;
# HTTP(S)_PROXY (egress) and ANTHROPIC_BASE_URL/OPENAI_BASE_URL (model injection) are picked up from env
# directly by git/curl and the agent CLIs, so no extra wiring is needed for those.
configure_broker() {
  [[ -n "${MINTOKEI_BROKER_CRED_URL:-}" ]] || return 0
  git config --global credential.helper /usr/local/bin/git-credential-broker
}
configure_broker

# Broker egress: the sandbox's ONLY route out is the per-session broker's CONNECT proxy (HTTPS_PROXY). The broker
# (a separate container/Pod) may still be starting when we boot, so WAIT for its proxy port to accept a connection
# before we clone the repo or the runner enrolls — both dial out THROUGH it, and a first attempt against a
# not-yet-listening broker fails with "connection refused" and the box exits (never comes online). Bounded; a
# best-effort continue after the bound so a genuinely-dead broker still surfaces as a clean enrollment error.
wait_for_broker() {
  local proxy="${HTTPS_PROXY:-${https_proxy:-}}"
  [[ -n "$proxy" ]] || return 0
  local hostport="${proxy#*://}"; hostport="${hostport%%/*}"
  local host="${hostport%%:*}" port="${hostport##*:}"
  [[ -n "$host" && -n "$port" && "$host" != "$port" ]] || return 0
  echo "sandbox-entrypoint: waiting for broker proxy ${host}:${port}..."
  for _ in $(seq 1 90); do
    if (exec 3<>"/dev/tcp/${host}/${port}") 2>/dev/null; then
      echo "sandbox-entrypoint: broker proxy reachable"
      return 0
    fi
    sleep 1
  done
  echo "sandbox-entrypoint: broker proxy ${host}:${port} still unreachable after 90s; continuing" >&2
}
wait_for_broker

if [[ -n "${SANDBOX_REPO_URL:-}" || -n "${SANDBOX_REPOS:-}" ]]; then
  prepare-workspace || { echo "sandbox-entrypoint: prepare-workspace failed" >&2; exit 1; }
fi

exec /opt/runner/Mintokei.Runner --data-dir /data "$@"
