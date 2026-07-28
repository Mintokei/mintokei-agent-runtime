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

# The sandbox container dials the host back to enroll. On a host with a default-deny INPUT policy (ufw, and
# most cloud images) those packets are DROPPED, and the only symptom is the runner's 100s HTTP timeout followed
# by "the sandbox exited before its agent runner could connect" — which looks like a container fault and is not.
# Check it from a real container, the same way the sandbox will, and say exactly how to fix it.
# Bounded: a DROP policy never sends a RST, so an unbounded connect hangs for the full TCP timeout (~2 min)
# instead of failing — the check has to give up on its own to be worth anything.
if ! docker run --rm --add-host host.docker.internal:host-gateway --entrypoint bash "$IMAGE" \
       -c "timeout 5 bash -c 'exec 3<>/dev/tcp/host.docker.internal/$PORT'" >/dev/null 2>&1; then
  cat >&2 <<EOF
A container on this host cannot reach the sample on port $PORT — the sandbox will never enroll.
This is a host firewall dropping container->host traffic, not a problem with the sample.

Allow the Docker bridge to reach the sample's two ports, e.g. with ufw:

  sudo ufw allow from 172.17.0.0/16 to any port $PORT,$((PORT + 1)) proto tcp

(remove it afterwards with the same command using 'delete allow')
EOF
  exit 1
fi

curl -sS -X POST "http://localhost:$PORT/demo/shared?prompt=Reply+with+one+word:+hello" || {
  echo "--- host log ---"; tail -40 /tmp/shared-sandbox-sample.log; exit 1; }
echo
