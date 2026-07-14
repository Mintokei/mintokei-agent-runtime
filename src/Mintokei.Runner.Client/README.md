# Mintokei.Runner.Client

The **worker** half of Mintokei's distributed execution: enroll with a backend, hold the gRPC link,
run agent CLIs locally via [`Mintokei.AgentEngine`](https://www.nuget.org/packages/Mintokei.AgentEngine),
and serve workspace files back to the backend over a tunnel.

Embed it in your own worker host with a single call, then run the host — several workers on one
machine just means pointing each at its own data directory.

```csharp
builder.Services.AddMintokeiRunner(builder.Configuration);
```

For a source-built worker executable, see `src/Mintokei.Runner` in the
[repository](https://github.com/Mintokei/mintokei-agent-runtime). The executable is included for
local/source builds; `Mintokei.Runner.Client` is the embeddable NuGet package. The backend side is
[`Mintokei.Runner.Host`](https://www.nuget.org/packages/Mintokei.Runner.Host).

Part of the **Mintokei Agent Runtime**.

## License

MIT
