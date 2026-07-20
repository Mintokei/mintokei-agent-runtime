# SandboxRunnerHostMinimal

A **genuinely-real** end-to-end sandbox host — **no `Fake*` types**. It hosts the real
`Mintokei.Runner.Host` backend (the exact wiring from [`RemoteRunnerMinimal`](../RemoteRunnerMinimal))
and adds `AddMintokeiSandbox`, then runs the full on-demand lifecycle over one HTTP endpoint:

```
POST /demo/sandbox-run?prompt=...&repo=<optional git url>
```

which **mints a real enrollment token → `docker run`s the sandbox image → waits for the in-container
runner to enroll over gRPC → dispatches an agent session into it → recycles the container.**

## Prerequisites (this one is NOT "runs anywhere")

Unlike the other samples it launches a real container, so it needs:

- **Docker** running on this host.
- The **sandbox image** present/pullable — build `Dockerfile.sandbox` (repo root) and set `Sandbox:Image`
  (default `ghcr.io/mintokei/mintokei-sandbox:latest`).
- The container to **reach this host**: the runner dials `Sandbox:BackendUrl` (REST enroll) and
  `Sandbox:GrpcBackendUrl` (gRPC control). Defaults use `host.docker.internal` (mapped into the
  container by the dev-only `AddHostGateway` → `--add-host=host.docker.internal:host-gateway`), which
  resolves to the host on Docker Desktop and on Linux.
- **Agent credentials** available to the CLI inside the container (baked into the image, or seeded via
  the request's `ClaudeConfigHostDir` / `GitCredentialsHostDir`), so a turn can actually run.

```bash
dotnet run --project samples/SandboxRunnerHostMinimal
# then, in another shell:
curl -X POST "http://localhost:5082/demo/sandbox-run?prompt=say%20hello%20from%20the%20sandbox"
```

## How it maps to a real product

Every call here is the same API a product uses — there are **no fakes** (contrast
[`SandboxSessionMinimal`](../SandboxSessionMinimal), which fakes the runtime + backend so it runs with
zero infra). The only thing a product adds on top is persistence + policy: which task gets a sandbox,
per-workspace repos/credentials, a warm pool, and a reaper — see the "what you reuse vs. implement"
table in [`src/Mintokei.Sandbox/README.md`](../../src/Mintokei.Sandbox/README.md).

| Step in `/demo/sandbox-run` | Library type used (reused, not faked) |
|---|---|
| Mint one-time token, pre-create the ephemeral machine | `IRunnerEnrollment.CreateEnrollmentTokenAsync` (Runner.Host) |
| Launch the container | `SandboxManager.ProvisionAsync` → `DockerSandboxRuntime` (Sandbox) |
| Detect the runner came Online | `IAgentControlPlane.IsRunnerConnected` (AgentControlPlane) |
| Run the session inside it | `IAgentControlPlane.StartSessionAsync(spec, machineId)` |
| Recycle | `SandboxManager.RecycleAsync` → `docker rm` |
