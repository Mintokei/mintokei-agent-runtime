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
#
# Every copy goes through seed_copy. Under `set -e` a bare failing `cp` kills the container with nothing but
# "cp: ... Permission denied", which the control plane can only report as "exited before its runner could
# connect" — and the usual cause (host creds bind-mounted raw, so 0600 root-owned files are unreadable to this
# non-root uid) is then indistinguishable from a failed clone. Name it instead.
seed_copy() {  # $1 = source under /seed, $2 = destination in HOME
  if cp -a "$1" "$2" 2>/dev/null; then
    return 0
  fi
  if [[ ! -r "$1" ]]; then
    echo "sandbox-entrypoint: cannot read '$1' as uid $(id -u) — the host credentials appear to be" >&2
    echo "sandbox-entrypoint: bind-mounted raw. They are root-owned 0600/0700 on the host, so they must be" >&2
    echo "sandbox-entrypoint: staged as a copy owned by uid $(id -u) before being mounted." >&2
  else
    echo "sandbox-entrypoint: failed to copy '$1' -> '$2'" >&2
  fi
  return 1
}

seed_creds() {
  [[ -d /seed ]] || return 0
  if [[ -f /seed/.claude/.credentials.json || -f /seed/.claude.json ]]; then
    mkdir -p "${HOME:-/root}/.claude"
    if [[ -f /seed/.claude/.credentials.json ]]; then
      seed_copy /seed/.claude/.credentials.json "${HOME:-/root}/.claude/.credentials.json" || return 1
    fi
    if [[ -f /seed/.claude.json ]]; then
      seed_copy /seed/.claude.json "${HOME:-/root}/.claude.json" || return 1
    fi
  fi
  if [[ -d /seed/.codex ]]; then
    mkdir -p "${HOME:-/root}/.codex"
    seed_copy /seed/.codex/. "${HOME:-/root}/.codex/" || return 1
  fi

  # Git credentials for cloning a private repo over the network (GitCredentialsHostDir mounted at
  # /seed/git). Supports an HTTPS token store (.git-credentials) and/or an SSH key dir (.ssh/).
  if [[ -d /seed/git ]]; then
    if [[ -f /seed/git/.git-credentials ]]; then
      seed_copy /seed/git/.git-credentials "${HOME:-/root}/.git-credentials" || return 1
      chmod 600 "${HOME:-/root}/.git-credentials" 2>/dev/null || true
      git config --global credential.helper store 2>/dev/null || true
    fi
    if [[ -d /seed/git/.ssh ]]; then
      mkdir -p "${HOME:-/root}/.ssh"
      seed_copy /seed/git/.ssh/. "${HOME:-/root}/.ssh/" || return 1
      chmod 700 "${HOME:-/root}/.ssh" 2>/dev/null || true
      chmod 600 "${HOME:-/root}"/.ssh/* 2>/dev/null || true
    fi
  fi
  return 0
}
seed_creds || { echo "sandbox-entrypoint: credential seeding failed — see the error above" >&2; exit 1; }

# Persist the agent-CLI session transcript across sandbox recycles so `--resume` keeps working. When a session
# goes idle the reaper tears the container down and re-provisions a fresh one on the next turn; the per-task
# workspace volume (mintokei-ws-<task>, mounted at /repos) survives, but Claude's conversation transcript lives
# under ~/.claude/projects on the container's EPHEMERAL layer and would die with the container — so the resume
# would fail with "no conversation found". Relocate the transcript dirs onto the persistent volume via a symlink
# (a hidden dir at the /repos mount root, sibling of the checked-out repo → outside any git tree). Harmless when
# /repos is ephemeral (e.g. no persistent workspace): the transcript simply stays ephemeral, exactly as before.
relink_dir() {  # $1 = CLI state dir (the link), $2 = its persistent home (the target)
  local link="$1" target="$2"
  mkdir -p "$(dirname "$link")" "$target" 2>/dev/null || return 0
  # Migrate a pre-existing REAL dir onto the volume once (e.g. first turn already wrote a transcript), then
  # replace it with a symlink so all future writes land on the persistent volume.
  if [[ -e "$link" && ! -L "$link" ]]; then
    cp -a "$link/." "$target/" 2>/dev/null || true
    rm -rf "$link" 2>/dev/null || true
  fi
  ln -sfn "$target" "$link"
}
link_session_state() {
  local root="${SANDBOX_SESSION_STATE_DIR:-/repos/.agent-session}"; root="${root%/}"
  # Bail quietly if the base isn't writable (keeps a non-writable /repos from failing the whole boot).
  mkdir -p "$root" 2>/dev/null || return 0
  relink_dir "${HOME:-/root}/.claude/projects" "$root/claude/projects"
  relink_dir "${HOME:-/root}/.codex/sessions"  "$root/codex/sessions"
}
link_session_state

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
