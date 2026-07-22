# BrokerSandboxMinimal

A coding-agent session in a **broker-egress ("hardened")** sandbox on a remote worker — the strongest posture
in the runtime. Same shape as [`RemoteSandboxMinimal`](../RemoteSandboxMinimal), but the profile selects
`SandboxEgress.Broker`, so **no secret ever enters the sandbox**:

- **Deny-by-default egress** — the sandbox runs on a per-session `--internal` Docker network with no route out
  except a per-session **broker** container. It can reach *only* the hosts in `EgressAllowlist`.
- **Git credentials it never stores** — a git credential helper fetches a token from the broker on demand; no
  `~/.git-credentials`, no SSH key on disk.
- **A model key it never holds** — the agent's model base URL points at the broker, which injects the API key
  and re-originates over TLS.

`RemoteSandboxManager.LaunchAsync(..., profile: "hardened", brokerSecrets: ...)` does it all: start the broker
+ network, `docker run` the sandbox joined to that network, wait online. Disposal recycles the sandbox **and**
the broker.

## Prerequisites

1. **A connected worker** with Docker, reachable over the control channel (enroll it exactly as in
   `RemoteSandboxMinimal` — `dotnet run --project src/Mintokei.Runner -- --backend ... --token ...`).
2. **Two images on the worker**:
   - the sandbox image (`Sandbox:Image`) — built from `Dockerfile.sandbox` (bakes in the git-credential helper),
   - the broker image (`Sandbox:BrokerImage`) — built from `Dockerfile.broker`:
     ```
     docker build -f Dockerfile.broker -t mintokei/sandbox-broker:latest .
     ```
3. **An `https://` backend reachable from the worker, and its host in `EgressAllowlist`.** In broker mode the
   in-container runner dials the backend **through the broker's CONNECT proxy**, which only tunnels TLS — a
   plaintext `http://` URL is rejected (fail-closed), and the backend host must be allowlisted or the runner
   can't connect. (This is why the sample is not a same-host `localhost` demo like `RemoteSandboxMinimal`.)

## Configure (`appsettings.json`, env `Sandbox__*`, or CLI)

- `Sandbox:Profiles:hardened:EgressAllowlist` — the **only** hosts the sandbox may reach: your backend, the git
  host, package registries, the model API.
- `Sandbox:BackendUrl` / `Sandbox:GrpcBackendUrl` — `https://`, host allowlisted.
- `Sandbox:GitCredentials` — `host=user:token` lines the broker injects (prefer a short-lived, repo-scoped
  token, e.g. a GitHub App installation token).
- `Sandbox:ModelUpstream` + `Sandbox:ModelAuth` — optional model injection (e.g. `https://api.anthropic.com` +
  `x-api-key=sk-ant-...`).

## Run

```bash
dotnet run --project samples/BrokerSandboxMinimal
# → http://localhost:5086
GET  /demo/workers
POST /demo/broker-sandbox-run?host=<worker-id>&repo=<private-git-url>&prompt=summarize this repo
```

The agent runs with full autonomy inside a disposable box that can't reach anything off the allowlist and holds
none of your long-lived secrets.

## Not "runs anywhere"

Needs a connected worker with Docker + both images, and an https/allowlisted backend reachable from that
worker. See the prerequisites above.
