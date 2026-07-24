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
- `Sandbox:AnthropicOAuthToken` — optional Anthropic subscription OAuth token (`sk-ant-oat…`). The broker
  injects it on the sandbox's model calls; no header is hand-formatted (see below).
- `Sandbox:GitHubToken` — optional GitHub token minted for the Copilot CLI.

These map to the broker's injected secrets in [`DemoBrokerSecrets`](DemoBrokerSecrets.cs) — an
`ISandboxBrokerSecretsProvider` that builds `SandboxBrokerSecrets` with the library's convention helpers
(`ModelUpstreamSpec.AnthropicOAuth(token)`, `SandboxBrokerSecrets.GitCredentialLine(...)`, `WithGitHubToken(...)`)
so no consumer re-derives header shapes. It's registered with `AddMintokeiSandboxBrokerSecrets<DemoBrokerSecrets>()`
and the runtime calls it at provision time. A real product implements this same interface, sourcing each session's
credentials from its own per-tenant store (keyed off `SandboxSessionRequest.Name`) instead of config.

## Run (remote worker)

```bash
dotnet run --project samples/BrokerSandboxMinimal
# → http://localhost:5086
GET  /demo/workers
POST /demo/broker-sandbox-run?host=<worker-id>&repo=<private-git-url>&prompt=summarize this repo
```

The agent runs with full autonomy inside a disposable box that can't reach anything off the allowlist and holds
none of your long-lived secrets.

## Full local loop (this machine, no worker)

Set **`Sandbox:LocalDocker=true`** and the broker + sandbox run on your local Docker daemon with **no enrolled
worker** — `AddMintokeiLocalCommandRunner()` makes the remote-sandbox path dispatch to `localhost`. `host` is
then ignored:

```bash
Sandbox__LocalDocker=true dotnet run --project samples/BrokerSandboxMinimal
POST /demo/broker-sandbox-run?repo=<git-url>&prompt=hi
```

**The one hard requirement is TLS**, and it's the same constraint as remote mode: the in-container runner dials
the backend **through the broker's CONNECT proxy, which only tunnels TLS**, so the backend must be `https` and
the sandbox must **trust its certificate**. On a dev box that means a self-signed cert whose CA is baked into
the sandbox image. Concretely:

1. **Config** — `Sandbox:LocalDocker=true`; put your backend host in `EgressAllowlist`; set `BackendUrl` /
   `GrpcBackendUrl` to your `https` URL (a hostname the broker container can reach — e.g. the docker-bridge
   host address, with that name/IP in the cert's SAN).
2. **Images on the local daemon**:
   - the broker: `docker build -f Dockerfile.broker -t mintokei/sandbox-broker:latest .`
   - a sandbox image (from `Dockerfile.sandbox`) with your **dev CA added to its trust store**
     (`COPY ca.crt /usr/local/share/ca-certificates/ && update-ca-certificates`).
3. **Backend on https** — run this sample with a Kestrel https endpoint bound to that cert.
4. **POST** `/demo/broker-sandbox-run` — the broker + sandbox launch locally, and the runner comes online
   **through the broker**.

**Verified vs. environment-specific:** the worker-free path itself — broker + sandbox containers launching on
this machine's Docker with no worker — is covered by an integration test (`LocalBrokerIntegrationTests`, opt-in
`MINTOKEI_SANDBOX_DOCKER_ITEST=1`). The runner's transport through the broker (HTTP/2 over TLS via CONNECT) is
verified separately. The steps above assemble those into a full session; the TLS cert / SAN / CA-trust wiring
is environment-specific, so treat it as the production-shaped setup, not a copy-paste one-liner.

## Not "runs anywhere"

Broker mode needs Docker + both images and an `https`, allowlisted, sandbox-trusted backend — locally (above)
or on a worker. That TLS requirement is deliberate: it's what keeps the runner's dial-back inside the
deny-by-default boundary.
