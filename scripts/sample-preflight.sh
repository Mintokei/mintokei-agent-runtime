#!/usr/bin/env bash
# Shared prerequisite checks for the samples that launch a real sandbox container.
#
# Sourced by the per-sample run scripts. Every check here corresponds to a failure that is otherwise
# reported far from its cause — a container that "exited before its agent runner could connect" is the same
# message whether the image is missing, the credentials are unreadable, or the host is unreachable.
#
#   require_docker <image>
#   require_claude_credentials
#   require_container_can_reach_host <port>   # call AFTER the sample is listening
#
# Each prints the exact command that fixes it and returns non-zero.

require_docker() {
  local image="$1"
  command -v docker >/dev/null || { echo "docker not found on PATH" >&2; return 1; }
  docker info >/dev/null 2>&1 || {
    echo "cannot talk to the Docker daemon as $(id -un) — is it running, and are you in the docker group?" >&2
    return 1
  }
  docker image inspect "$image" >/dev/null 2>&1 || {
    echo "missing sandbox image '$image'. Pull it:" >&2
    echo "    docker pull $image" >&2
    echo "  or build it from the repo root:" >&2
    echo "    docker build -f Dockerfile.sandbox -t $image ." >&2
    return 1
  }
}

require_claude_credentials() {
  export Sandbox__ClaudeConfigHostDir="${Sandbox__ClaudeConfigHostDir:-$HOME/.claude}"
  export Sandbox__ClaudeConfigJsonHostFile="${Sandbox__ClaudeConfigJsonHostFile:-$HOME/.claude.json}"
  [ -d "$Sandbox__ClaudeConfigHostDir" ] || {
    echo "no Claude credentials at $Sandbox__ClaudeConfigHostDir — log in with the claude CLI first," >&2
    echo "or point Sandbox__ClaudeConfigHostDir at a directory that has them." >&2
    return 1
  }
  # Root-owned 0600 credentials are normal and fine: the runtime stages a uid-readable copy before mounting
  # them, because the sandbox runs as a non-root uid. Nothing to check beyond existence.
}

# The sandbox dials the host back to enroll. Two independent things break that, with different symptoms:
#   * a listener bound to 127.0.0.1  -> "Connection refused"  (host reachable, nothing on that interface)
#   * a default-deny INPUT firewall  -> a 100s timeout        (packets dropped, no RST)
# Both surface only as "the sandbox exited before its agent runner could connect", so check from a real
# container, the same way the sandbox will.
require_container_can_reach_host() {
  local port="$1" image="${2:-${IMAGE:-ghcr.io/mintokei/mintokei-sandbox:latest}}"
  # Bounded: a DROP policy sends no RST, so an unbounded connect hangs for the full TCP timeout.
  docker run --rm --add-host host.docker.internal:host-gateway --entrypoint bash "$image" \
    -c "timeout 5 bash -c 'exec 3<>/dev/tcp/host.docker.internal/$port'" >/dev/null 2>&1 && return 0

  cat >&2 <<EOF

A container on this host cannot reach the sample on port $port — the sandbox will never enroll.

Two possible causes:

  1. The sample is bound to 127.0.0.1 instead of 0.0.0.0. A container reaches the host on the bridge
     gateway, not loopback. Check the Kestrel URLs in the sample's appsettings.json.

  2. A host firewall is dropping container->host traffic (ufw and most cloud images default to
     deny-all inbound). Allow the Docker bridge to reach this sample's ports:

         sudo ufw allow from 172.17.0.0/16 to any port $port,$((port + 1)) proto tcp

     and remove it afterwards with the same command using 'delete allow'.
EOF
  return 1
}

# Wait until the sample's HTTP endpoint answers at all (any status: it is listening that matters).
wait_for_sample() {
  local port="$1" tries="${2:-60}"
  for _ in $(seq 1 "$tries"); do
    curl -s -o /dev/null "http://localhost:$port/" && return 0
    sleep 1
  done
  echo "sample did not start listening on port $port" >&2
  return 1
}
