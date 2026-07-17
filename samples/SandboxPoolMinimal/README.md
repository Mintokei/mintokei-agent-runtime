# SandboxPoolMinimal

Standalone demo of the `Mintokei.Sandbox` pool layer — **no product, no backend, no Docker required.**

It wires `AddMintokeiSandbox` (in-code options), swaps the real `DockerSandboxRuntime` for a logging
fake so it runs anywhere, supplies a demo `ISandboxSessionSource`, and drives the pool tick
(`SandboxPoolService.RunOnceAsync`) to show: warm-pool top-up, one-shot recycle, and
reap-and-refill in a single tick.

```bash
dotnet run --project samples/SandboxPoolMinimal
```

In production you keep the default `DockerSandboxRuntime` (registered by `AddMintokeiSandbox`) and
provide an `ISandboxSessionSource` that mints real enrollment tokens against your backend — see the
Mintokei API for a reference implementation.
