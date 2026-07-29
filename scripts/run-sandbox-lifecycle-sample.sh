#!/usr/bin/env bash
# Runs SandboxLifecycleExplicit end to end — the same lifecycle as SandboxRunnerHostMinimal with every step
# written out by hand (mint token → docker run → wait online → dispatch → recycle).
set -euo pipefail
cd "$(dirname "$0")/.."
# shellcheck source=scripts/sample-preflight.sh
source scripts/sample-preflight.sh

IMAGE="${SANDBOX_IMAGE:-ghcr.io/mintokei/mintokei-sandbox:latest}"
PORT=5086
REPO="${SAMPLE_REPO:-https://github.com/octocat/Hello-World.git}"
PROMPT="${SAMPLE_PROMPT:-what file is in this repo?}"

require_docker "$IMAGE"
require_claude_credentials

dotnet run --project samples/SandboxLifecycleExplicit >/tmp/sandbox-lifecycle-sample.log 2>&1 &
APP=$!
trap 'kill $APP 2>/dev/null || true' EXIT

wait_for_sample "$PORT" || { tail -40 /tmp/sandbox-lifecycle-sample.log; exit 1; }
require_container_can_reach_host "$PORT" "$IMAGE"

curl -sS -X POST "http://localhost:$PORT/demo/sandbox-run" \
  --get --data-urlencode "repo=$REPO" --data-urlencode "prompt=$PROMPT" || {
    echo "--- host log ---"; tail -40 /tmp/sandbox-lifecycle-sample.log; exit 1; }
echo
