# Mintokei.Runner.Client

The **worker** half of Mintokei's distributed execution: enroll with a backend, hold the gRPC link,
run agent CLIs locally via [`Mintokei.AgentEngine`](https://www.nuget.org/packages/Mintokei.AgentEngine),
and serve workspace files back to the backend over a tunnel.

Embed it in your own worker host, run the one-time startup path, then run the host. Several workers
on one machine just means pointing each at its own data directory.

```csharp
builder.Services.AddMintokeiRunner(builder.Configuration);

var host = builder.Build();
await host.Services.EnsureRunnerReadyAsync();
await host.RunAsync();
```

`EnsureRunnerReadyAsync()` is required: it initializes the local outbox and completes first-boot
enrollment before the background transports start.

## Required configuration

Bind a `Runner` section or configure `RunnerOptions` in code. The important settings are:

- `BackendUrl` — the backend's enrollment/token-exchange base URL.
- `EnrollmentToken` — the one-time token for first boot. Cleared after a successful enrollment.
- `GrpcBackendUrl` — optional override for the gRPC endpoint; usually only needed in local plaintext dev where HTTP/1 and HTTP/2 are split across ports.
- `DataDir` — where credentials and the local outbox live. Set this when running multiple workers on one machine.
- `Name` — optional display name reported during enrollment.

Example:

```json
{
  "Runner": {
    "BackendUrl": "http://localhost:5080",
    "GrpcBackendUrl": "http://localhost:5081",
    "EnrollmentToken": "<one-time-token>",
    "DataDir": "./runner-data",
    "Name": "worker-1"
  }
}
```

If you prefer code-based configuration:

```csharp
builder.Services.AddMintokeiRunner(options =>
{
    options.BackendUrl = "http://localhost:5080";
    options.GrpcBackendUrl = "http://localhost:5081";
    options.EnrollmentToken = "<one-time-token>";
    options.DataDir = "./runner-data";
    options.Name = "worker-1";
});
```

For a source-built worker executable, see `src/Mintokei.Runner` in the
[repository](https://github.com/Mintokei/mintokei-agent-runtime). The executable is included for
local/source builds; `Mintokei.Runner.Client` is the embeddable NuGet package. The backend side is
[`Mintokei.Runner.Host`](https://www.nuget.org/packages/Mintokei.Runner.Host).

Part of the **Mintokei Agent Runtime**.

## License

MIT
