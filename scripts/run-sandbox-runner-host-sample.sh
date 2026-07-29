#!/usr/bin/env bash
# Runs SandboxRunnerHostMinimal end to end: checks prerequisites, starts the host, runs a real agent turn
# in a real container, and stops everything again.
set -euo pipefail
cd "$(dirname "$0")/.."
# shellcheck source=scripts/sample-preflight.sh
source scripts/sample-preflight.sh

IMAGE="${SANDBOX_IMAGE:-ghcr.io/mintokei/mintokei-sandbox:latest}"
PORT=5082
REPO="${SAMPLE_REPO:-https://github.com/octocat/Hello-World.git}"
PROMPT="${SAMPLE_PROMPT:-what file is in this repo?}"

require_docker "$IMAGE"
require_claude_credentials

dotnet run --project samples/SandboxRunnerHostMinimal >/tmp/sandbox-runner-host-sample.log 2>&1 &
APP=$!
trap 'kill $APP 2>/dev/null || true' EXIT

wait_for_sample "$PORT" || { tail -40 /tmp/sandbox-runner-host-sample.log; exit 1; }
require_container_can_reach_host "$PORT" "$IMAGE"

# A real turn needs BOTH credentials and a repo: the session starts in /repos/<name>, which only exists once
# a repo has been cloned into the container.
curl -sS -X POST "http://localhost:$PORT/demo/sandbox-run" \
  --get --data-urlencode "repo=$REPO" --data-urlencode "prompt=$PROMPT" || {
    echo "--- host log ---"; tail -40 /tmp/sandbox-runner-host-sample.log; exit 1; }
echo
