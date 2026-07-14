# Mintokei.AgentControlPlane

Orchestration layer over [`Mintokei.AgentEngine`](https://www.nuget.org/packages/Mintokei.AgentEngine):
spawn, track, and **capacity-manage many agent-CLI sessions** across one or more machines, with
admission control — without inheriting any product schema.

Where the engine owns *one* CLI session, the control plane owns *many*: it launches sessions from a
fully-populated spec, tracks them under a caller-chosen session key, accounts for per-machine capacity,
and admits or rejects new work accordingly. The caller supplies the persistence and the meaning of a
"session key" (e.g. your own task id) through small seams — the control plane never interprets it.

## Quick start

For a runnable local orchestration sample, see
[`samples/ControlPlaneLocal`](../../samples/ControlPlaneLocal).

`AddAgentControlPlane()` registers the control plane itself; you supply the backend(s) it can spawn, a
process runner (local here — swap in `Mintokei.Runner.Host`'s factory for remote runners), and logging:

```csharp
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine;                 // IAgentBackend, AgentSessionSpec
using Mintokei.AgentEngine.AgentTools;      // AgentToolKey
using Mintokei.AgentEngine.Claude;          // ClaudeBackend
using Mintokei.AgentEngine.CommandRunner;   // ICommandLineRunnerFactory, LocalCommandLineRunnerFactory

services.AddLogging();
services.AddAgentControlPlane();
services.AddSingleton<IAgentBackend, ClaudeBackend>();                       // one per tool you'll run
services.AddSingleton<ICommandLineRunnerFactory, LocalCommandLineRunnerFactory>();
```

Then spawn and drive sessions through the one front door, `IAgentControlPlane`:

```csharp
var controlPlane = provider.GetRequiredService<IAgentControlPlane>();

var key  = Guid.NewGuid();   // your own opaque key
var spec = new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli, WorkingDirectory = "/path/to/repo" };

// Pass a runnerMachineId to run it on a connected remote runner instead of locally.
await using var session = await controlPlane.StartSessionAsync(key, spec);
await session.SendMessageAsync("Summarise this repository.");

await foreach (var evt in session.Output) { /* MessageOutput / DeltaOutput / TurnEnded / … */ }

await controlPlane.StopSessionAsync(key);
```

Admission/capacity is enforced as you start sessions (per-machine counts + in-flight claims); the
advanced `ICapacityLedger` seam exposes the slot book for your own limit logic and idle eviction.

Part of the **Mintokei Agent Runtime**. See the
[repository](https://github.com/Mintokei/mintokei-agent-runtime) for the full stack (engine,
control plane, and the remote-runner host/client).

## License

MIT
