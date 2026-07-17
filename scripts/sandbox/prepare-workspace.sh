#!/usr/bin/env bash
# Provision a source git repo INSIDE the sandbox container from a read-only bare
# mirror using git alternates (`clone --shared`), so:
#   * provisioning is offline + near-instant (objects borrowed, not copied), and
#   * the agent's new commits land in the container-local object store (writable),
#     while reads fall through to the RO mirror.
# After this runs, Mintokei's existing host-driven `git worktree add <src>/../mkwt/<branch>`
# flow works unchanged. See docs/sandboxed-runner-isolation-plan.md §4.5.
#
# Driven by env (set per-container at `docker run`):
#   SANDBOX_REPO_URL     (required) real remote, e.g. https://github.com/acme/app.git
#   SANDBOX_SOURCE_PATH  (required) where the source repo should live, e.g. /repos/app
#   SANDBOX_REPO_BRANCH  (optional) base branch to check out (default: mirror HEAD)
#   SANDBOX_MIRROR_DIR   (optional) RO bare-mirror root (default: /repo-cache)
#   SANDBOX_GIT_NAME / SANDBOX_GIT_EMAIL (optional) commit identity for the agent
set -euo pipefail

REPO_URL="${SANDBOX_REPO_URL:-}"
SOURCE_PATH="${SANDBOX_SOURCE_PATH:-}"
BRANCH="${SANDBOX_REPO_BRANCH:-}"
MIRROR_DIR="${SANDBOX_MIRROR_DIR:-/repo-cache}"
[[ -n "$REPO_URL" && -n "$SOURCE_PATH" ]] || {
  echo "prepare-workspace: need SANDBOX_REPO_URL and SANDBOX_SOURCE_PATH" >&2; exit 2; }

# Stable mirror name from the URL — MUST match repo-mirror-sidecar.sh's sanitize().
sanitize() { printf '%s' "$1" | sed -E 's#^[a-zA-Z]+://##; s#^[^/@]+[:@]##; s#/#__#g; s#\.git$##'; }
MIRROR="$MIRROR_DIR/$(sanitize "$REPO_URL").git"

# Ephemeral sandbox: borrowed objects live under a RO mirror (possibly a different
# uid), so silence git's dubious-ownership guard globally.
git config --global --add safe.directory '*' 2>/dev/null || true
git config --global user.name  "${SANDBOX_GIT_NAME:-Mintokei Sandbox}"       2>/dev/null || true
git config --global user.email "${SANDBOX_GIT_EMAIL:-sandbox@mintokei.local}" 2>/dev/null || true

# Idempotent: already provisioned -> just fetch and exit.
if git -C "$SOURCE_PATH" rev-parse --git-dir >/dev/null 2>&1; then
  echo "prepare-workspace: $SOURCE_PATH already a repo; fetching origin"
  git -C "$SOURCE_PATH" fetch --quiet origin || true
  exit 0
fi

mkdir -p "$(dirname "$SOURCE_PATH")"
if [[ -d "$MIRROR" ]]; then
  echo "prepare-workspace: clone --shared from mirror $MIRROR (offline, borrows objects)"
  git clone --shared "$MIRROR" "$SOURCE_PATH"
  git -C "$SOURCE_PATH" remote set-url origin "$REPO_URL"   # push/fetch hit the real remote
else
  echo "prepare-workspace: WARN no mirror at $MIRROR — falling back to a network clone" >&2
  git clone "$REPO_URL" "$SOURCE_PATH"
fi

if [[ -n "$BRANCH" ]]; then
  git -C "$SOURCE_PATH" fetch --quiet origin "$BRANCH" 2>/dev/null || true
  git -C "$SOURCE_PATH" checkout "$BRANCH" 2>/dev/null \
    || git -C "$SOURCE_PATH" checkout -b "$BRANCH" 2>/dev/null || true
fi

echo "prepare-workspace: ready at $SOURCE_PATH (HEAD $(git -C "$SOURCE_PATH" rev-parse --short HEAD 2>/dev/null || echo '?'))"
