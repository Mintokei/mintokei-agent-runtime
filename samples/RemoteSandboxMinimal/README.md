# RemoteSandboxMinimal

Runs **one agent session in a sandbox on a *remote* worker** — the distributed twin of
[`SandboxRunnerHostMinimal`](../SandboxRunnerHostMinimal) (which runs the container on the same host).
It's the only sample that exercises `AddMintokeiRemoteSandbox()` → `RemoteDockerSandboxRuntime` +
`SandboxCredentialStager`: the container is `docker run` on a **chosen, already-connected worker** over the
control channel, non-root, with its credentials staged uid-readable — **no Fake\* types**.

The whole backend is three calls:

```csharp
builder.AddMintokeiRunnerHost().AddClaude();          // Runner.Host + control plane + gRPC + JWT + SQLite
builder.Services.AddMintokeiSandbox(builder.Configuration);   // spec factory + isolation profiles
builder.Services.AddMintokeiRemoteSandbox();          // RemoteDockerSandboxRuntime + SandboxCredentialStager
// ...
app.MapMintokeiRunnerHost();
```

plus one endpoint that dispatches the full lifecycle to a worker (mint token → **stage creds on the worker** →
`docker run` there → wait for the in-container runner to connect back → dispatch the session → recycle).

## Prerequisites

This is **not** "runs anywhere" — it dispatches a real container to a real worker:

1. **This backend** running (`dotnet run --project samples/RemoteSandboxMinimal`), on `:5084` (HTTP enroll) +
   `:5085` (HTTP/2 gRPC).
2. **A connected worker** — a machine running the runner, pointed at this backend:
   ```bash
   Runner__GrpcBackendUrl=http://<backend-host>:5085 \
     dotnet run --project src/Mintokei.Runner -- \
       --backend http://<backend-host>:5084 --token <enrollment-token> --data-dir ./runner-data
   ```
   (The backend prints a one-time enrollment token on boot; the runner dials out, no inbound port.)
3. **Docker on the worker** + the **sandbox image** present/pullable there (`Sandbox:Image`, default
   `ghcr.io/mintokei/mintokei-sandbox:latest`; or build `Dockerfile.sandbox`).
4. **URLs reachable from the worker** — `Sandbox:BackendUrl` / `Sandbox:GrpcBackendUrl` are dialed by the
   container *on the worker*. Same-host dev: the `host.docker.internal` defaults + `Sandbox:AddHostGateway=true`.
   Different machine: set them to this backend's **LAN address** and `AddHostGateway=false`.
5. **Agent credentials** — leave unset to use the **worker's own** `~/.claude` / `~/.codex` / git creds (probed
   via `$HOME`), or point `Sandbox:ClaudeConfigHostDir` etc. at paths on the worker. Whatever they resolve to is
   **staged into a uid-readable copy on the worker** (so the non-root container can read them) and mounted RO.
   Without valid credentials the runner enrolls and the session dispatches, but the CLI can't complete a turn.
6. **A repo** — the session runs in `/repos/<name>`, which exists only once a repo is cloned into the container.

## Run

```bash
dotnet run --project samples/RemoteSandboxMinimal
# → connect a worker (step 2 above), then find its id:
curl http://localhost:5084/demo/workers
# → run a turn in a sandbox on that worker:
curl -X POST "http://localhost:5084/demo/remote-sandbox-run?host=<worker-id>&repo=https://github.com/octocat/Hello-World.git&prompt=what%20file%20is%20in%20this%20repo%3F"
# -> [Assistant/AgentMessage] The repo contains a single README.md file ...
```

## How it maps to a real product

Every call is the same API a product uses — this is a condensed, DB-free version of the product's
`SandboxSessionAssigner.AssignOnHostAsync`: `RemoteDockerSandboxRuntime` for provisioning + per-session
volumes, `SandboxCredentialStager` for uid-isolated credentials, and `IAgentControlPlane` to dispatch the
session and stream it back. A product adds the persistence (which workspace → which repo/creds, task pins,
ephemeral-machine GC) around these seams.
