# Tools

Runnable programs built on the runtime libraries. Unlike [`samples/`](../samples), these are meant to
be used rather than read — a sample shows you how an API works, a tool does a job.

| Tool | What it does | Needs |
|---|---|---|
| [`Mintokei.Hermod`](Mintokei.Hermod) (`hermod`) | Pick a session from one agent CLI — by description, not by GUID — and carry on with it in another. | an installed CLI with sessions in the directory |

```bash
dotnet tool install -g Mintokei.Hermod   # then: hermod
dotnet run --project tools/Mintokei.Hermod   # or from a clone, without installing
```

`Mintokei.Hermod` is the package; `hermod` is the command it puts on your PATH. It ships from
the same lockstep `VersionPrefix` as the libraries, through the same `publish.yml`.
