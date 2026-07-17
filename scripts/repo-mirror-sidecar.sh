#!/usr/bin/env bash
# Per-host bare-mirror manager for the sandbox repo cache. Mirrors are the git
# alternates source that sandbox containers borrow objects from (mounted read-only
# as /repo-cache). Keeping them fresh makes `clone --shared` inside a container an
# offline, near-instant operation. See docs/sandboxed-runner-isolation-plan.md §4.4.
#
#   repo-mirror-sidecar.sh ensure  <repo-url> [<repo-url> ...]  # create/update named mirrors
#   repo-mirror-sidecar.sh refresh                              # update all existing mirrors
#   repo-mirror-sidecar.sh loop [interval-seconds]             # refresh forever (default 300)
#   repo-mirror-sidecar.sh path   <repo-url>                    # print the mirror path for a URL
#
# MIRROR_DIR overrides the cache root (default /repo-cache). Private repos need git
# credentials in the environment this runs in (SSH agent / GIT_ASKPASS / a helper).
#
# CAUTION: do not aggressively prune objects a live session still references. This
# uses `remote update --prune` (prunes refs, not reachable objects), which is safe.
set -euo pipefail

MIRROR_DIR="${MIRROR_DIR:-/repo-cache}"

# MUST match prepare-workspace.sh's sanitize() so names line up.
sanitize() { printf '%s' "$1" | sed -E 's#^[a-zA-Z]+://##; s#^[^/@]+[:@]##; s#/#__#g; s#\.git$##'; }
mirror_path() { printf '%s/%s.git' "$MIRROR_DIR" "$(sanitize "$1")"; }

ensure_one() {
  local url="$1" dir; dir="$(mirror_path "$1")"
  if [[ -d "$dir" ]]; then
    echo ">> refresh $dir"; git -C "$dir" remote update --prune
  else
    echo ">> clone --mirror $url -> $dir"; git clone --mirror "$url" "$dir"
  fi
}

refresh_all() {
  shopt -s nullglob
  local any=0
  for d in "$MIRROR_DIR"/*.git; do any=1; echo ">> refresh $d"; git -C "$d" remote update --prune || true; done
  [[ $any -eq 1 ]] || echo ">> no mirrors under $MIRROR_DIR yet"
}

cmd="${1:-}"; shift || true
case "$cmd" in
  ensure)  mkdir -p "$MIRROR_DIR"; [[ $# -gt 0 ]] || { echo "ensure needs at least one repo url" >&2; exit 2; }
           for u in "$@"; do ensure_one "$u"; done ;;
  refresh) refresh_all ;;
  loop)    iv="${1:-300}"; echo ">> refresh loop every ${iv}s (Ctrl-C to stop)"
           while true; do refresh_all; sleep "$iv"; done ;;
  path)    [[ $# -eq 1 ]] || { echo "path needs one repo url" >&2; exit 2; }; mirror_path "$1" ;;
  *) echo "usage: $0 {ensure <url...>|refresh|loop [interval]|path <url>}" >&2; exit 2 ;;
esac
