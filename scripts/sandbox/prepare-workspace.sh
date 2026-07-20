#!/usr/bin/env bash
# Provision one or more source git repos INSIDE the sandbox container from a read-only
# bare mirror using git alternates (`clone --shared`), so:
#   * provisioning is offline + near-instant (objects borrowed, not copied), and
#   * the agent's new commits land in the container-local object store (writable),
#     while reads fall through to the RO mirror.
# After this runs, Mintokei's existing host-driven `git worktree add <src>/../mkwt/<branch>`
# flow works unchanged. See docs/sandboxed-runner-isolation-plan.md §4.5.
#
# Driven by env (set per-container at `docker run`):
#   SANDBOX_REPOS        (preferred) ';'-separated records, each 'url|source_path|branch'
#                        (branch optional) — provisions EVERY repo listed, into its source_path.
#   SANDBOX_REPO_URL     (legacy single) real remote, e.g. https://github.com/acme/app.git
#   SANDBOX_SOURCE_PATH  (legacy single) where the source repo should live, e.g. /repos/app
#   SANDBOX_REPO_BRANCH  (legacy single) base branch to check out (default: mirror HEAD)
#   SANDBOX_MIRROR_DIR   (optional) RO bare-mirror root (default: /repo-cache)
#   SANDBOX_GIT_NAME / SANDBOX_GIT_EMAIL (optional) commit identity for the agent
set -euo pipefail

MIRROR_DIR="${SANDBOX_MIRROR_DIR:-/repo-cache}"

# Stable mirror name from the URL — MUST match repo-mirror-sidecar.sh's sanitize().
sanitize() { printf '%s' "$1" | sed -E 's#^[a-zA-Z]+://##; s#^[^/@]+[:@]##; s#/#__#g; s#\.git$##'; }

# Ephemeral sandbox: borrowed objects live under a RO mirror (possibly a different
# uid), so silence git's dubious-ownership guard globally + set a commit identity (once).
git config --global --add safe.directory '*' 2>/dev/null || true
git config --global user.name  "${SANDBOX_GIT_NAME:-Mintokei Sandbox}"       2>/dev/null || true
git config --global user.email "${SANDBOX_GIT_EMAIL:-sandbox@mintokei.local}" 2>/dev/null || true

# Provision ONE repo into source_path: reuse it if already present (fetch — the persistent-workspace
# volume / a prior run), else `clone --shared` from the mirror (offline) or fall back to a network
# clone, then check out the requested branch.
provision_one() {
  local repo_url="$1" source_path="$2" branch="${3:-}"
  [[ -n "$repo_url" && -n "$source_path" ]] || {
    echo "prepare-workspace: need a url + source_path (got url='$repo_url' path='$source_path')" >&2; return 2; }
  local mirror="$MIRROR_DIR/$(sanitize "$repo_url").git"

  # Idempotent: already provisioned -> just fetch and return (never re-clone / clobber the agent's tree).
  if git -C "$source_path" rev-parse --git-dir >/dev/null 2>&1; then
    echo "prepare-workspace: $source_path already a repo; fetching origin"
    git -C "$source_path" fetch --quiet origin || true
    return 0
  fi

  mkdir -p "$(dirname "$source_path")"
  if [[ -d "$mirror" ]]; then
    echo "prepare-workspace: clone --shared from mirror $mirror (offline, borrows objects)"
    git clone --shared "$mirror" "$source_path"
    git -C "$source_path" remote set-url origin "$repo_url"   # push/fetch hit the real remote
  else
    echo "prepare-workspace: WARN no mirror at $mirror — falling back to a network clone of $repo_url" >&2
    git clone "$repo_url" "$source_path"
  fi

  if [[ -n "$branch" ]]; then
    git -C "$source_path" fetch --quiet origin "$branch" 2>/dev/null || true
    git -C "$source_path" checkout "$branch" 2>/dev/null \
      || git -C "$source_path" checkout -b "$branch" 2>/dev/null || true
  fi

  echo "prepare-workspace: ready at $source_path (HEAD $(git -C "$source_path" rev-parse --short HEAD 2>/dev/null || echo '?'))"
}

if [[ -n "${SANDBOX_REPOS:-}" ]]; then
  # ';'-separated records, each 'url|source_path|branch' (branch may be empty).
  IFS=';' read -ra _records <<< "$SANDBOX_REPOS"
  for _rec in "${_records[@]}"; do
    [[ -z "$_rec" ]] && continue
    IFS='|' read -r _url _path _branch <<< "$_rec"
    provision_one "$_url" "$_path" "$_branch"
  done
else
  # Legacy single-repo env (the dev spike script). The product path always sets SANDBOX_REPOS.
  provision_one "${SANDBOX_REPO_URL:-}" "${SANDBOX_SOURCE_PATH:-}" "${SANDBOX_REPO_BRANCH:-}"
fi
