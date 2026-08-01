# Tools

Runnable programs built on the runtime libraries. Unlike [`samples/`](../samples), these are meant to
be used rather than read — a sample shows you how an API works, a tool does a job.

| Tool | What it does | Needs |
|---|---|---|
| [`Mintokei.AgentMove`](Mintokei.AgentMove) (`agentmove`) | Pick a session from one agent CLI — by description, not by GUID — and carry on with it in another. | an installed CLI with sessions in the directory |

```bash
dotnet run --project tools/Mintokei.AgentMove
```

None of these are published to NuGet. `agentmove` could become a
`dotnet tool install -g` command by flipping `PackAsTool` in its csproj; that is deliberately off
until its command line has settled.
