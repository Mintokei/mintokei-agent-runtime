# SharedSandboxMinimal

**Two sessions, one sandbox.** Provisions a sandbox once, runs a real agent turn in it, attaches a second
session to the *same* container through the admission gate, and then shows the gate **refusing** a different
tool — which is the half that matters.

## Why the gate exists

A sandbox has ONE broker on ONE network, and the broker cannot tell which session a connection came from. Its
egress allowlist and injected credentials apply to **every** session inside. Admitting a tool the sandbox was
not built for silently grants the sessions already running that tool's network reach and credentials — for the
sandbox's whole life, because the allowlist is fixed when the broker starts.

## Prerequisites

| | |
|---|---|
| Docker | running, and reachable as the current user (`docker ps`) |
| the sandbox image | `docker pull ghcr.io/mintokei/mintokei-sandbox:latest` |
| agent credentials | a `~/.claude` (or `~/.codex`) that is already logged in — **without these the turn fails**, because the in-container CLI starts unauthenticated |
| host reachable from the container | `AddHostGateway: true` gives the container `host.docker.internal`; the default on Linux and Docker Desktop |

## Run it

```bash
# 1. point the sample at your credentials (mounted read-only, copied into the container's HOME)
export Sandbox__ClaudeConfigHostDir="$HOME/.claude"
export Sandbox__ClaudeConfigJsonHostFile="$HOME/.claude.json"

# 2. start the host
dotnet run --project samples/SharedSandboxMinimal

# 3. in another shell — provision once, run two sessions in it, and try a third with a different tool
curl -X POST 'http://localhost:5092/demo/shared?prompt=Reply+with+one+word:+hello'
```

`scripts/run-shared-sandbox-sample.sh` does all three.

## What to look for

```jsonc
{
  "sandbox": "sandbox-…",              // ONE sandbox
  "first":   "hello",                  // session 1 ran in it
  "second":  "hello",                  // session 2 ran in the SAME container
  "refusal": "sandbox '…' serves [ClaudeCodeCli] and cannot host a 'CodexCli' session — …"
}
```

While it runs, `docker ps` shows **one** sandbox container, not two.

## Notes

- The sample uses `IAgentControlPlane.StartSessionAsync(…, runnerMachineId: …)`, **not**
  `SandboxAgentHost.RunAsync`. The facade provisions a sandbox per call and disposes it — right for one-shot
  work, and it silently defeats sharing.
- The sandbox is left running when the request returns, so you can inspect it. Remove it with
  `docker rm -f <name>`.
