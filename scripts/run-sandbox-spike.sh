#!/usr/bin/env bash
# Phase 0 spike: run ONE agent session inside a container wrapping Mintokei.Runner.
# See docs/sandboxed-runner-isolation-plan.md (§8, Phase 0). NOT production-hardened.
#
# What this validates:
#   1. The runner binary runs in a container, dials OUT, enrolls, and shows up
#      ONLINE in the UI (Runners) — proves image + networking + dial-out.
#   2. (optional) An agent CLI runs a task against a bind-mounted workspace.
#
# Get an enrollment token from the WebApp: Runners -> Add.
#
# Usage (dev API on the host):
#   scripts/run-sandbox-spike.sh \
#       --backend http://host.docker.internal:5192 \
#       --grpc-backend http://host.docker.internal:5191 \
#       --token <enrollment-token> \
#       [--name spike-1] \
#       [--workspace /abs/path/to/a/test/repo] \
#       [--claude-config "$HOME/.claude"]
#
# Notes:
#   * --grpc-backend is only needed against a `dotnet run` dev API, which serves
#     h2c gRPC on a separate loopback port (5191). Against a real ingress the gRPC
#     and HTTP URLs are the same, so omit it.
#   * The data dir is a tmpfs, so each run enrolls a FRESH machine. To reuse one
#     machine identity, swap the tmpfs for a named volume (see RUN_ARGS below).
#   * Phase-0-only shortcuts: runs as root with open egress so the spike Just
#     Works. Phase 1 hardens (see the plan doc).
set -euo pipefail

BACKEND=""; TOKEN=""; NAME="sandbox-spike"; WORKSPACE=""; CLAUDE_CONFIG=""; GRPC_BACKEND=""
REPO=""; BRANCH=""; SOURCE_PATH=""; REPO_CACHE=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --backend)       BACKEND="$2";       shift 2 ;;
    --grpc-backend)  GRPC_BACKEND="$2";  shift 2 ;;
    --token)         TOKEN="$2";         shift 2 ;;
    --name)          NAME="$2";          shift 2 ;;
    --workspace)     WORKSPACE="$2";     shift 2 ;;
    --claude-config) CLAUDE_CONFIG="$2"; shift 2 ;;
    # Step 2 — provision a source repo inside the container from a RO mirror.
    --repo)          REPO="$2";          shift 2 ;;
    --branch)        BRANCH="$2";        shift 2 ;;
    --source-path)   SOURCE_PATH="$2";   shift 2 ;;
    --repo-cache)    REPO_CACHE="$2";    shift 2 ;;
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 1 ;;
  esac
done
[[ -n "$BACKEND" && -n "$TOKEN" ]] || { echo "need --backend and --token (UI -> Runners -> Add)" >&2; exit 1; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

case "$(uname -m)" in
  x86_64|amd64)  RID="linux-x64" ;;
  aarch64|arm64) RID="linux-arm64" ;;
  *) echo "unsupported arch $(uname -m)" >&2; exit 1 ;;
esac

IMAGE="mintokei/sandbox:spike"
echo ">> building $IMAGE ($RID) — first build is slow (SDK restore + npm installs) ..."
DOCKER_BUILDKIT=1 docker build \
  -f "$REPO_ROOT/Dockerfile.sandbox" \
  --build-arg RID="$RID" \
  -t "$IMAGE" \
  "$REPO_ROOT"

RUN_ARGS=(
  --rm -it
  --name "mk-$NAME"
  # Reach a dev API running on the host. In prod this is a single ingress URL.
  --add-host host.docker.internal:host-gateway
  # Ephemeral runner state (creds + outbox). Swap for `-v mk-$NAME-data:/data`
  # to persist the machine identity across runs.
  --tmpfs /data
)
# Runner:* config goes via CLI FLAGS (appended after the image) because env vars
# for keys present in appsettings.json (BackendUrl, EnrollmentToken) are shadowed.
# GrpcBackendUrl has no flag and is absent from appsettings, so env DOES bind it.
CLI_ARGS=( --backend "$BACKEND" --token "$TOKEN" --name "$NAME" )
[[ -n "$GRPC_BACKEND" ]] && RUN_ARGS+=(-e Runner__GrpcBackendUrl="$GRPC_BACKEND")

# Step 2: provision a source repo inside the container from a RO bare mirror via git
# alternates (the entrypoint runs prepare-workspace before the runner connects). Point
# the UI workspace's WorkingDirectory at --source-path; worktrees are created there.
if [[ -n "$REPO" ]]; then
  SRC="${SOURCE_PATH:-/repos/$(basename "${REPO%.git}")}"
  RUN_ARGS+=(-e SANDBOX_REPO_URL="$REPO" -e SANDBOX_SOURCE_PATH="$SRC")
  [[ -n "$BRANCH" ]] && RUN_ARGS+=(-e SANDBOX_REPO_BRANCH="$BRANCH")
  if [[ -n "$REPO_CACHE" ]]; then
    REPO_CACHE="$(cd "$REPO_CACHE" && pwd)"
    RUN_ARGS+=(-v "$REPO_CACHE:/repo-cache:ro")
    echo ">> provision $REPO -> $SRC ${BRANCH:+(branch $BRANCH) }from mirror $REPO_CACHE"
  else
    echo ">> provision $REPO -> $SRC ${BRANCH:+(branch $BRANCH) }(no --repo-cache: will network-clone)"
  fi
fi

# Optional: bind-mount a workspace at its SAME absolute path, so the host-supplied
# WorkingDirectory resolves inside the container (Phase 0 == bind-mounted worktree).
# Point the workspace's path in the UI at this same path. Files the agent writes
# will be root-owned on the host (spike caveat).
if [[ -n "$WORKSPACE" ]]; then
  WORKSPACE="$(cd "$WORKSPACE" && pwd)"
  RUN_ARGS+=(-v "$WORKSPACE:$WORKSPACE")
  echo ">> workspace mounted at $WORKSPACE (set the UI workspace path to match)"
fi

# Optional: give the agent CLI its auth. Phase 1 does this per-tenant, read-only.
if [[ -n "$CLAUDE_CONFIG" ]]; then
  CLAUDE_CONFIG="$(cd "$CLAUDE_CONFIG" && pwd)"
  RUN_ARGS+=(-e HOME=/root -v "$CLAUDE_CONFIG:/root/.claude:ro")
  echo ">> mounted $CLAUDE_CONFIG -> /root/.claude (ro)"
fi

echo ">> starting sandbox '$NAME' -> $BACKEND"
echo ">> watch the UI: Runners should show '$NAME' ONLINE, then start a task on it."
echo ">> Ctrl-C to stop (--rm cleans the container up)."
exec docker run "${RUN_ARGS[@]}" "$IMAGE" "${CLI_ARGS[@]}"
