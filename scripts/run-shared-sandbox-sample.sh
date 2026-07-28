#!/usr/bin/env bash
# Runs SharedSandboxMinimal end to end: checks prerequisites, starts the host, calls the demo endpoint.
set -euo pipefail
cd "$(dirname "$0")/.."

IMAGE="${SANDBOX_IMAGE:-ghcr.io/mintokei/mintokei-sandbox:latest}"
PORT=5092

command -v docker >/dev/null || { echo "docker not found"; exit 1; }
docker info >/dev/null 2>&1 || { echo "docker not reachable as $(id -un)"; exit 1; }
docker image inspect "$IMAGE" >/dev/null 2>&1 || { echo "missing image: docker pull $IMAGE"; exit 1; }

# Credentials: without them the in-container CLI starts unauthenticated and the agent turn fails.
export Sandbox__ClaudeConfigHostDir="${Sandbox__ClaudeConfigHostDir:-$HOME/.claude}"
export Sandbox__ClaudeConfigJsonHostFile="${Sandbox__ClaudeConfigJsonHostFile:-$HOME/.claude.json}"
[ -d "$Sandbox__ClaudeConfigHostDir" ] || { echo "no credentials at $Sandbox__ClaudeConfigHostDir"; exit 1; }

dotnet run --project samples/SharedSandboxMinimal >/tmp/shared-sandbox-sample.log 2>&1 &
APP=$!
trap 'kill $APP 2>/dev/null || true' EXIT

for _ in $(seq 1 60); do
  curl -sf "http://localhost:$PORT/health" >/dev/null 2>&1 && break
  curl -s "http://localhost:$PORT/" >/dev/null 2>&1 && break
  sleep 1
done

curl -sS -X POST "http://localhost:$PORT/demo/shared?prompt=Reply+with+one+word:+hello" || {
  echo "--- host log ---"; tail -40 /tmp/shared-sandbox-sample.log; exit 1; }
echo
