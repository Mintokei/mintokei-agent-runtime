#!/usr/bin/env bash
# Runs RemoteSandboxMinimal end to end ON ONE MACHINE.
#
# The sample dispatches a sandbox to a *remote worker*, which normally means a second machine. It doesn't have
# to: a worker is just the runner process dialing out, so this starts one locally against the sample's own
# backend. The code path exercised is the real distributed one (stage creds on the worker → docker run there →
# wait for the in-container runner to dial back) — only the worker's address is loopback.
set -euo pipefail
cd "$(dirname "$0")/.."
# shellcheck source=scripts/sample-preflight.sh
source scripts/sample-preflight.sh

IMAGE="${SANDBOX_IMAGE:-ghcr.io/mintokei/mintokei-sandbox:latest}"
PORT=5084
GRPC=5085
REPO="${SAMPLE_REPO:-https://github.com/octocat/Hello-World.git}"
PROMPT="${SAMPLE_PROMPT:-what file is in this repo?}"
RUNNER_DATA="$(mktemp -d)"   # NOT the machine's real runner data dir — this worker is throwaway

require_docker "$IMAGE"
require_claude_credentials

cleanup() {
  [ -n "${RUNNER:-}" ] && kill "$RUNNER" 2>/dev/null || true
  [ -n "${APP:-}" ] && kill "$APP" 2>/dev/null || true
  rm -rf "$RUNNER_DATA"
}
trap cleanup EXIT

dotnet run --project samples/RemoteSandboxMinimal >/tmp/remote-sandbox-sample.log 2>&1 &
APP=$!

wait_for_sample "$PORT" || { tail -40 /tmp/remote-sandbox-sample.log; exit 1; }
require_container_can_reach_host "$PORT" "$IMAGE"

# The worker token is minted on boot; take a fresh one from the endpoint so a restart can't hand us a stale one.
TOKEN=$(curl -sS -X POST "http://localhost:$PORT/demo/enroll-token" | tr -d '"')
[ -n "$TOKEN" ] || { echo "could not mint a worker enrollment token" >&2; exit 1; }

echo "starting a local worker (data dir: $RUNNER_DATA)..."
Runner__GrpcBackendUrl="http://localhost:$GRPC" \
  dotnet run --project src/Mintokei.Runner -- \
    --backend "http://localhost:$PORT" --token "$TOKEN" --data-dir "$RUNNER_DATA" \
    >/tmp/remote-sandbox-worker.log 2>&1 &
RUNNER=$!

# Wait for it to show up as connected — the sample dispatches BY worker id, so there is nothing to target
# until the control channel is live.
WORKER=""
for _ in $(seq 1 60); do
  WORKER=$(curl -sS "http://localhost:$PORT/demo/workers" | tr -d '[]" ' | cut -d, -f1)
  [ -n "$WORKER" ] && break
  sleep 1
done
[ -n "$WORKER" ] || {
  echo "no worker connected after 60s" >&2
  echo "--- worker log ---"; tail -30 /tmp/remote-sandbox-worker.log
  echo "--- backend log ---"; tail -20 /tmp/remote-sandbox-sample.log
  exit 1
}
echo "worker connected: $WORKER"

curl -sS -X POST "http://localhost:$PORT/demo/remote-sandbox-run" \
  --get --data-urlencode "host=$WORKER" --data-urlencode "repo=$REPO" --data-urlencode "prompt=$PROMPT" || {
    echo "--- backend log ---"; tail -40 /tmp/remote-sandbox-sample.log; exit 1; }
echo
