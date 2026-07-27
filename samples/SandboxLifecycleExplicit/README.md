# SandboxLifecycleExplicit

The sandbox lifecycle **written out step by step** — no `Fake*` types, and no facade either.

> **Copy [`SandboxRunnerHostMinimal`](../SandboxRunnerHostMinimal) instead** if you just want to run an agent
> in a sandbox: `SandboxAgentHost.RunAsync()` does everything below in one call.
>
> This sample exists for when you need to **own one of the steps** — your own admission control, a custom
> online-wait or provisioning telemetry, a warm pool, a reaper. The facade is a convenience over exactly
> these public APIs, not a wall, and this is what driving them yourself looks like.

```
POST /demo/sandbox-run?prompt=...&repo=<optional git url>
```

1. Mint a one-time enrollment token — `IRunnerEnrollment.CreateEnrollmentTokenAsync(isEphemeral: true)`,
   which **pre-creates the machine identity** so the session binds to a known id instead of racing to
   discover the runner by name.
2. `docker run` the sandbox image — `SandboxManager.ProvisionAsync`.
3. Wait for the in-container runner to enroll + connect — poll `IAgentControlPlane.IsRunnerConnected`
   alongside `SandboxManager.GetStatusAsync`, so a container that exits during startup ends the wait
   instead of burning the timeout. (The facade does this event-driven, off `RunnerConnected`.)
4. Dispatch the session into it — `IAgentControlPlane.StartSessionAsync(spec, runnerMachineId)`, the same
   `IAgentSession` API as any other runner.
5. Recycle — `SandboxManager.RecycleAsync`.

## Prerequisites (this one is NOT "runs anywhere")

Same as `SandboxRunnerHostMinimal`: **Docker**, the **sandbox image** (`Sandbox:Image`), the container able
to **reach this host** (`Sandbox:BackendUrl` / `Sandbox:GrpcBackendUrl`, defaulting to `host.docker.internal`
via the dev-only `AddHostGateway`), and optionally **agent credentials** so the CLI can authenticate.

It listens on **5086/5087** so it can run alongside the other sandbox samples.

```bash
dotnet run --project samples/SandboxLifecycleExplicit
curl -X POST "http://localhost:5086/demo/sandbox-run?repo=https://github.com/octocat/Hello-World.git&prompt=hi"
```

> A real turn needs **both** credentials *and* a `repo` — the session runs in `/repos/<name>`, which only
> exists once a repo has been cloned into the container.
