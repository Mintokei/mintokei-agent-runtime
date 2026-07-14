# Mintokei.Runner.Host

The **backend** half of Mintokei's distributed execution: accept a fleet of dial-in worker machines
over gRPC and dispatch agent CLIs to them, streaming their output back over a durable outbox. Embeds
[`Mintokei.AgentEngine`](https://www.nuget.org/packages/Mintokei.AgentEngine) and
[`Mintokei.AgentControlPlane`](https://www.nuget.org/packages/Mintokei.AgentControlPlane).

It is coupled to your application only through a small optional callback interface, `IRunnerHost` —
the transport raises events (runner connected, installed CLIs reported, process orphaned,
disconnected, …) and your app reacts to the ones it cares about. Nothing here references product
concepts; task lifecycle, capacity accounting, and workspace semantics stay in *your* code.

```csharp
builder.Services.AddRunnerHostCore(o => o.CliProbesProvider = ...);
// optionally: builder.Services.AddSingleton<IRunnerHost, MyRunnerHost>();
```

That snippet is only one piece of the host. A working backend also needs:

- a `RunnerHostDbContext` database registration for enrollment, presence, and the durable outbox
- `AddRunnerHostServer(...)` plus `MapRunnerHost()` for enrollment and runner-token minting
- JWT auth that validates the runner `machine_id` claim on the gRPC data plane
- `AddAgentControlPlane()` plus at least one `IAgentBackend`
- gRPC registration and a mapped `RunnerLinkService`

The smallest complete composition is in [`samples/RemoteRunnerMinimal`](../../samples/RemoteRunnerMinimal).
That sample shows the full host wiring: database, auth, enrollment, control plane, and demo endpoints.

See [`samples/RemoteRunnerMinimal`](https://github.com/Mintokei/mintokei-agent-runtime/tree/main/samples/RemoteRunnerMinimal)
for the smallest possible backend that accepts remote runners.

Part of the **Mintokei Agent Runtime**. The worker side is
[`Mintokei.Runner.Client`](https://www.nuget.org/packages/Mintokei.Runner.Client).

## License

MIT
