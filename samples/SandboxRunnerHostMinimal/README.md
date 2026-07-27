# SandboxRunnerHostMinimal

A **genuinely-real** end-to-end sandbox host — **no `Fake*` types**. Two calls make the backend, one call
runs the agent:

```csharp
builder.AddSandboxAgentHost().AddClaude();   // Mintokei.Sandbox.Hosting: db + transport + JWT + control plane + gRPC + sandbox
// ...
app.MapSandboxAgentHost();                    // auth + enroll routes + gRPC data plane

await using var run = await host.RunAsync(new SandboxAgentRequest { Repo = repo, Prompt = prompt });
var turn = await run.CollectTurnAsync();      // ← disposal stops the session and removes the container
```

`RunAsync` owns the whole lifecycle: **mint a one-time enrollment token (pre-creating the ephemeral machine
identity) → launch the sandbox → wait for the in-container runner to enroll over gRPC → dispatch an agent
session into it → stream it → recycle on dispose.** All settings — image, URLs, credentials, JWT — bind from
the `RunnerHost` and `Sandbox` config sections (appsettings / env vars like
`Sandbox__BackendUrl` / CLI args); every value has a sensible default.

```
POST /demo/sandbox-run?prompt=...&repo=<optional git url>
```

## Any backend, same code

The backend is a **deployment choice, not a code choice** — `Program.cs` is identical for all three:

| Where the sandbox runs | How |
|---|---|
| **Local Docker** | `Sandbox:Backend=docker` (this sample's default) |
| **Kubernetes** | `Sandbox:Backend=kubernetes` + `Sandbox:KubernetesNamespace` |
| **A remote worker** (nested Docker) | `.AddRemoteWorkers()` + set `SandboxAgentRequest.HostMachineId` |

## Prerequisites (this one is NOT "runs anywhere")

Unlike the other samples it launches a real container, so it needs:

- **Docker** running on this host.
- The **sandbox image** present/pullable — build `Dockerfile.sandbox` (repo root) and set `Sandbox:Image`
  (default `ghcr.io/mintokei/mintokei-sandbox:latest`).
- The container to **reach this host**: the runner dials `Sandbox:BackendUrl` (REST enroll) and
  `Sandbox:GrpcBackendUrl` (gRPC control). Defaults use `host.docker.internal` (mapped into the
  container by the dev-only `AddHostGateway` → `--add-host=host.docker.internal:host-gateway`), which
  resolves to the host on Docker Desktop and on Linux.
- **Agent credentials** for the CLI inside the container — otherwise the runner enrolls and the session
  dispatches, but the `claude`/`codex` CLI exits before its handshake (no auth). Point the optional
  `Sandbox:ClaudeConfigHostDir` / `ClaudeConfigJsonHostFile` (and `CodexConfigHostDir` /
  `GitCredentialsHostDir`) config keys at host paths; each is mounted RO at `/seed` and copied into the
  container's HOME by the entrypoint. Leave them unset for a plumbing-only run.

```bash
# plumbing-only (runner enrolls + session dispatches; the agent turn won't run — see the note below):
dotnet run --project samples/SandboxRunnerHostMinimal
curl -X POST "http://localhost:5082/demo/sandbox-run?prompt=say%20hello"

# a REAL agent turn — seed the host's Claude credentials AND pass a repo (so the session has a workdir):
Sandbox__ClaudeConfigHostDir="$HOME/.claude" \
Sandbox__ClaudeConfigJsonHostFile="$HOME/.claude.json" \
  dotnet run --project samples/SandboxRunnerHostMinimal
curl -X POST "http://localhost:5082/demo/sandbox-run?repo=https://github.com/octocat/Hello-World.git&prompt=what%20file%20is%20in%20this%20repo%3F"
# -> [Assistant/AgentMessage] The repo contains a single README.md file ...
```

> A real turn needs **both**: **credentials** (so the CLI authenticates) *and* a **`repo`** — the
> session runs in `/repos/<name>`, which only exists once a repo has been cloned into the container.
> With no repo the runner still enrolls and the session still dispatches, but the CLI has no valid
> working directory to start in.

## How it maps to a real product

Every call here is the same API a product uses — there are **no fakes** (contrast
[`SandboxSessionMinimal`](../SandboxSessionMinimal), which fakes the runtime + backend so it runs with
zero infra). The only thing a product adds on top is persistence + policy: which task gets a sandbox,
per-workspace repos/credentials, a warm pool, and a reaper — see the "what you reuse vs. implement"
table in [`src/Mintokei.Sandbox/README.md`](../../src/Mintokei.Sandbox/README.md).

`SandboxAgentHost.RunAsync` sequences the library calls a product would otherwise sequence by hand:

| Step inside `RunAsync` | Library type used (reused, not faked) |
|---|---|
| Mint one-time token, pre-create the ephemeral machine | `IRunnerEnrollment.CreateEnrollmentTokenAsync` (Runner.Host) |
| Launch the container / pod | `SandboxManager.ProvisionAsync` → `ISandboxRuntime` (Sandbox) |
| …or launch it on a connected worker | `RemoteSandboxManager.LaunchAsync` (Sandbox) |
| Detect the runner came Online | `IAgentControlPlane.RunnerConnected` + `IsRunnerConnected` (AgentControlPlane) |
| Run the session inside it | `IAgentControlPlane.StartSessionAsync(spec, machineId)` |
| Recycle | `SandboxManager.RecycleAsync` / `RemoteSandboxSession.DisposeAsync` |

Need to drive those steps yourself (custom admission, your own wait/telemetry, a warm pool)? They stay
public — `RunAsync` is a convenience over them, not a wall. See
[`SandboxLifecycleExplicit`](../SandboxLifecycleExplicit), which is this same lifecycle with every step
written out.

Per-run values (broker egress needs, per-tenant credential paths, a machine-local repo mirror) go through the
`configure` hook rather than the host-wide options:

```csharp
await using var run = await host.RunAsync(request, r => r with {
    Broker = new SandboxBrokerNeeds(["anthropic"], Git: true, Allowlist: tool.Allowlist),
    AddHostGateway = false,
    ClaudeConfigHostDir = tenant.ClaudeDir,
}, ct);
```
