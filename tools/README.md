# Tools

Runnable programs built on the runtime libraries. Unlike [`samples/`](../samples), these are meant to
be used rather than read — a sample shows you how an API works, a tool does a job.

| Tool | What it does | Needs |
|---|---|---|
| [`Mintokei.AgentMove`](Mintokei.AgentMove) (`agentmove`) | Pick a session from one agent CLI — by description, not by GUID — and carry on with it in another. | an installed CLI with sessions in the directory |

```bash
dotnet tool install -g Mintokei.AgentMove   # then: agentmove
dotnet run --project tools/Mintokei.AgentMove   # or from a clone, without installing
```

`Mintokei.AgentMove` is the package; `agentmove` is the command it puts on your PATH. It ships from
the same lockstep `VersionPrefix` as the libraries, through the same `publish.yml`.
