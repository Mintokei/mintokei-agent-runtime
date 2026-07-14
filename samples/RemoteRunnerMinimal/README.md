# RemoteRunnerMinimal

The smallest host that accepts a **remote Mintokei runner** over gRPC and runs agent CLIs
(Claude Code, …) on it — built on `Mintokei.Runner.Host` + `Mintokei.AgentControlPlane` +
`Mintokei.AgentEngine`, with **none** of a full product host's extra weight.

If you only want to drive a CLI on the current machine, start with
[`samples/LocalAgentMinimal`](../LocalAgentMinimal). If you want local multi-session orchestration,
start with [`samples/ControlPlaneLocal`](../ControlPlaneLocal). This sample is the distributed
backend/worker path.

## What makes it "minimal"

| Concern | This sample |
|---|---|
| **`IRunnerHost` reactions** (reconnect-resume, installed-CLI persistence, file-watch, orphan GC) | **Not implemented at all.** `AddRunnerHostCore` falls back to the library's `NullRunnerHost`. |
| **Product database** | **None.** `RunnerHostDbContext` runs on throwaway in-memory SQLite (`EnsureCreated` once, gone on exit). No file, no migrations, no product tables. |
| **CLI-probe list** | One options lambda (`CliProbesProvider`) so the runner discovers Claude — optional; leave it unset for zero discovery. |
| **`IRemoteProcessRecovery`** | A no-op (recovery only matters across a host restart, which this sample never does). |

Everything else — runner presence, the durable outbox, output streaming — is the library.
The whole host is one `Program.cs`.

## Run it

```bash
dotnet run --project samples/RemoteRunnerMinimal
```

On boot it creates the in-memory schema, binds two Kestrel endpoints
(`http://localhost:5080` HTTP/1 for enrollment/token exchange under `/api` + `http://localhost:5081` HTTP/2 for gRPC —
they can't share a plaintext port), and **prints a one-time enrollment token** to the console.

## Attach a runner

Point a runner at the host with that token. gRPC needs the HTTP/2 port, so set `GrpcBackendUrl`:

```bash
Runner__GrpcBackendUrl=http://localhost:5081 \
  dotnet run --project src/Mintokei.Runner -- \
  --backend http://localhost:5080 \
  --token <enrollment-token> \
  --data-dir ./runner-data
```

The runner does enroll → token-exchange → gRPC connect on its own. When it lands you'll see
`Runner connected: <machineId>` in the host console (the control plane's `RunnerConnected` event).

> The runner shells out to the agent CLIs, so **`claude` must be installed and authenticated on
> the runner's machine** for the run below to succeed.

## Run a task on the runner

```bash
# List connected runners
curl http://localhost:5080/demo/runners

# Run one prompt on the first connected runner (returns that turn's transcript)
curl -X POST "http://localhost:5080/demo/run?prompt=List%20the%20files%20here&dir=/tmp"

# Mint another enrollment token
curl -X POST http://localhost:5080/demo/enroll-token
```

`/demo/run` calls `IAgentControlPlane.StartSessionAsync(spec, runnerMachineId)` — the exact same
session API as a local spawn, only with a machine id — and streams the CLI's output back over gRPC.

## How it maps to the libraries

- `AddRunnerHostCore(o => o.CliProbesProvider = …)` — the transport (outbox pump, gRPC registries,
  remote command-runner factory) + the optional `IRunnerHost` defaulting to `NullRunnerHost`.
- `AddRunnerHostServer(o => o.SigningKey = …)` + `MapRunnerHost()` — enrollment + runner-token minting.
- `AddJwtBearer("RunnerJwt")` + the `"Runner"` policy — validates the runner's `machine_id` JWT on the
  gRPC data plane.
- `AddAgentControlPlane()` + one `IAgentBackend` — spawns/tracks sessions; composes the remote
  `ICommandLineRunnerFactory` from `AddRunnerHostCore`.
- `RunnerHostDbContext` on in-memory SQLite — the outbox/enrollment/presence state.

To make it even more minimal, drop the `CliProbesProvider` lambda (the runner just reports 0 CLIs;
task execution is unaffected).
