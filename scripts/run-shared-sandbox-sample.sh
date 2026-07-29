#!/usr/bin/env bash
# Runs SharedSandboxMinimal end to end: two sessions in ONE sandbox, plus the refusal of a session whose tool
# the sandbox was not provisioned to serve.
set -euo pipefail
cd "$(dirname "$0")/.."
# shellcheck source=scripts/sample-preflight.sh
source scripts/sample-preflight.sh

IMAGE="${SANDBOX_IMAGE:-ghcr.io/mintokei/mintokei-sandbox:latest}"
PORT=5092
PROMPT="${SAMPLE_PROMPT:-Reply with one word: hello}"

require_docker "$IMAGE"
require_claude_credentials

dotnet run --project samples/SharedSandboxMinimal >/tmp/shared-sandbox-sample.log 2>&1 &
APP=$!
trap 'kill $APP 2>/dev/null || true' EXIT

wait_for_sample "$PORT" || { tail -40 /tmp/shared-sandbox-sample.log; exit 1; }
require_container_can_reach_host "$PORT" "$IMAGE"

curl -sS -X POST "http://localhost:$PORT/demo/shared" --get --data-urlencode "prompt=$PROMPT" || {
  echo "--- host log ---"; tail -40 /tmp/shared-sandbox-sample.log; exit 1; }
echo
