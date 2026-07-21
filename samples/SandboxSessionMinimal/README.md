# SandboxSessionMinimal

Standalone demo of running **one agent session in a sandbox** with `Mintokei.Sandbox` — the on-demand
path: build a session request → provision a per-session container → its in-container runner enrolls
back → dispatch a session to that machine → one-shot recycle. **No product, no backend, no Docker
required:** a fake `ISandboxRuntime` stands in for `docker run` and a fake backend stands in for
`Mintokei.Runner.Host`, so the whole lifecycle runs anywhere.

```bash
dotnet run --project samples/SandboxSessionMinimal
```

The two fakes are the only things you replace in production: keep the default `DockerSandboxRuntime`
(registered by `AddMintokeiSandbox`), provide an `ISandboxSessionSource` that mints real enrollment
tokens against your backend, and dispatch the session through `Mintokei.Runner.Host` /
`IAgentControlPlane` once the container's runner comes Online.

For a version with **no fakes** — real `Runner.Host` + a real container — see
[`SandboxRunnerHostMinimal`](../SandboxRunnerHostMinimal). For the warm-pool variant see
[`SandboxPoolMinimal`](../SandboxPoolMinimal); for the design see
[`docs/sandboxed-runner-isolation-plan.md`](../../docs/sandboxed-runner-isolation-plan.md).
