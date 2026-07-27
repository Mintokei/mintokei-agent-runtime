# Mintokei.Sandbox.Hosting

Run an agent CLI **inside a sandbox** — on local Docker, in Kubernetes, or on a remote worker — in one call.

This package is the seam between the two halves that already exist: the **transport** half
([`Mintokei.Runner.Host.Hosting`](../Mintokei.Runner.Host.Hosting) — enrollment, control plane, gRPC) and the
**isolation** half ([`Mintokei.Sandbox`](../Mintokei.Sandbox) — container/pod runtimes, profiles, egress
broker). It adds `SandboxAgentHost`, which sequences them.

## Quick start

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddSandboxAgentHost().AddClaude();   // + .AddCodex() / .AddRemoteWorkers()

var app = builder.Build();
app.MapSandboxAgentHost();

app.MapPost("/run", async (SandboxAgentHost host, string prompt, CancellationToken ct) =>
{
    await using var run = await host.RunAsync(new SandboxAgentRequest
    {
        Tool = AgentToolKey.ClaudeCodeCli,
        Repo = "https://github.com/acme/app",
        Prompt = prompt,
    }, ct);

    var turn = await run.CollectTurnAsync(ct);
    return Results.Text(turn.Transcript);
});                                           // ← disposal stops the session and removes the sandbox

app.Run();
```

`RunAsync` does the whole dance: mint a one-time enrollment token (pre-creating the machine identity so the
session binds by id, not by racing on a name) → launch the sandbox → wait for the in-container runner to
enroll and connect → start the agent session on that machine → send the prompt. Disposing the run stops the
session and recycles the sandbox — container, staged credentials and per-session broker included — on every
path, including failure and cancellation.

## Where it runs

Same code, three substrates:

| Target | How |
|---|---|
| **Local Docker** | `Sandbox:Backend=docker` |
| **Kubernetes** | `Sandbox:Backend=kubernetes` (+ `Sandbox:KubernetesNamespace`, in-cluster SA or kubeconfig) |
| **Remote worker** (nested Docker on a connected runner) | `.AddRemoteWorkers()` + `SandboxAgentRequest.HostMachineId` |

## Configuration

Two sections. `Sandbox` configures isolation (backend, image, profiles) — see
[`Mintokei.Sandbox`](../Mintokei.Sandbox). `SandboxAgentHost` configures the run:

| Key | Meaning |
|---|---|
| `BackendUrl` | **Required.** REST URL the runner *inside the container* dials. Must be reachable from there — not `localhost` unless the sandbox shares the host's network. |
| `GrpcBackendUrl` | Control-stream URL. Defaults to `BackendUrl` (fine when the same endpoint serves HTTP/2). |
| `AddHostGateway` | Docker dev convenience: maps `host.docker.internal` into the container. |
| `Profile` | Default isolation profile (`Sandbox:Profiles:<name>`). |
| `OnlineTimeoutSeconds` | How long to wait for the runner to connect (default 180 — cold image pulls dominate). |
| `ClaudeConfigHostDir`, `ClaudeConfigJsonHostFile`, `CodexConfigHostDir`, `GitCredentialsHostDir` | Host paths mounted read-only at `/seed` and copied into the sandbox's HOME. Without them the CLI has no credentials and the turn can't run. Ignored for broker-egress profiles, where the broker injects credentials and the box never sees them. |

## Beyond one turn

`RunAsync` returns a live session, not a one-shot:

```csharp
await using var run = await host.RunAsync(new SandboxAgentRequest { Tool = AgentToolKey.ClaudeCodeCli });

await run.SendMessageAsync("first task");
await foreach (var evt in run.Output)          // deltas, tool calls, permission prompts, turn boundaries
{
    if (evt is InteractionRequested ask) { /* answer via run.Session.RespondAsync(...) */ }
    if (evt is TurnEnded) break;
}
await run.SendMessageAsync("now do the follow-up");
```

Set `SessionKey` to register the session under your own task id, `SessionOptions` to choose the interaction
mode (auto-approve vs surface prompts), and `PersistentWorkspaceTaskId` (Kubernetes) to keep the working tree
and transcript across a pod recycle so the session can be resumed.

## Not a wall

This is a convenience over public APIs, not a replacement for them. If you need custom admission control, your
own wait/telemetry, a warm pool, or a reaper, keep using `IRunnerEnrollment`, `SandboxManager` /
`RemoteSandboxManager`, and `IAgentControlPlane` directly — `RunAsync` is exactly those calls in order.

See [`samples/SandboxRunnerHostMinimal`](../../samples/SandboxRunnerHostMinimal) for a complete, runnable host.
