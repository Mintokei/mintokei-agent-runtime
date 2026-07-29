#!/usr/bin/env bash
# Runs RemoteRunnerMinimal end to end ON ONE MACHINE: starts the host, attaches a local worker, runs one
# prompt on it. No Docker — this sample runs the agent CLI directly on the worker, not in a sandbox.
set -euo pipefail
cd "$(dirname "$0")/.."
# shellcheck source=scripts/sample-preflight.sh
source scripts/sample-preflight.sh

PORT=5080
GRPC=5081
PROMPT="${SAMPLE_PROMPT:-Reply with one word: hello}"
RUNNER_DATA="$(mktemp -d)"   # NOT the machine's real runner data dir — this worker is throwaway

cleanup() {
  [ -n "${RUNNER:-}" ] && kill "$RUNNER" 2>/dev/null || true
  [ -n "${APP:-}" ] && kill "$APP" 2>/dev/null || true
  rm -rf "$RUNNER_DATA"
}
trap cleanup EXIT

dotnet run --project samples/RemoteRunnerMinimal >/tmp/remote-runner-sample.log 2>&1 &
APP=$!

wait_for_sample "$PORT" || { tail -40 /tmp/remote-runner-sample.log; exit 1; }

TOKEN=$(curl -sS -X POST "http://localhost:$PORT/demo/enroll-token" | tr -d '"')
[ -n "$TOKEN" ] || { echo "could not mint an enrollment token" >&2; exit 1; }

echo "starting a local worker (data dir: $RUNNER_DATA)..."
Runner__GrpcBackendUrl="http://localhost:$GRPC" \
  dotnet run --project src/Mintokei.Runner -- \
    --backend "http://localhost:$PORT" --token "$TOKEN" --data-dir "$RUNNER_DATA" \
    >/tmp/remote-runner-worker.log 2>&1 &
RUNNER=$!

for _ in $(seq 1 60); do
  [ -n "$(curl -sS "http://localhost:$PORT/demo/runners" | tr -d '[]" ')" ] && break
  sleep 1
done
[ -n "$(curl -sS "http://localhost:$PORT/demo/runners" | tr -d '[]" ')" ] || {
  echo "no runner connected after 60s" >&2; tail -30 /tmp/remote-runner-worker.log; exit 1; }
echo "runner connected"

# The agent CLI runs on the WORKER, so it needs to be installed and authenticated there — here, this machine.
curl -sS -X POST "http://localhost:$PORT/demo/run" --get \
  --data-urlencode "prompt=$PROMPT" --data-urlencode "dir=$PWD" || {
    echo "--- host log ---"; tail -40 /tmp/remote-runner-sample.log; exit 1; }
echo
