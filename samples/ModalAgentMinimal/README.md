# Modal JSON inference sample

An independent host using the standard Mintokei runner, an explicitly selected Modal account, and
Claude API authentication. No Kunchi code or personal CLI home is required. It is a small integration
sample; production durable launch/account journals are described in the [provider guide](../../src/Mintokei.Sandbox.Modal).

From the runtime repository root:

```sh
python3 -m venv artifacts/modal/venv
artifacts/modal/venv/bin/pip install -r src/Mintokei.Sandbox.Modal/python/requirements.txt
dotnet publish src/Mintokei.Runner -c Release -r linux-x64 --self-contained true -o artifacts/modal/bundle/runner
dotnet run --project samples/ModalAgentMinimal
```

Supply these configuration keys through your deployment's secret/configuration mechanism (environment
variables use `__` in place of `:`):

| Key | Value |
| --- | --- |
| `Modal:TokenId`, `Modal:TokenSecret` | Explicit Modal API credentials |
| `Modal:Python` | Absolute path to the venv's Python |
| `Modal:Bundle` | Absolute path to `artifacts/modal/bundle` |
| `Sandbox:BackendUrl`, `Sandbox:GrpcBackendUrl` | Public HTTPS enrollment and HTTP/2 gRPC endpoints for this host |
| `Demo:AccessToken` | Secret required in the `X-Demo-Token` request header |
| `Demo:Model` | Claude model available to the configured API account |
| `Demo:AnthropicApiKey` | API key for this isolated example |

Serve the host behind an ingress supporting HTTP/2 gRPC and configure Kestrel appropriately, as in the
other runner-host samples. POST `{"prompt":"Return three labels for this task"}` to `/demo/infer`
with the demo header. The operation provisions one sandbox, returns JSON, and stops it. Real calls
incur Modal and model usage. The default backend registration is unused Docker infrastructure; this
request selects its Modal runtime explicitly and requires no Docker daemon.

For a production product, replace the sample API key environment with an authorized run-scoped model
gateway token, and add `IModalSandboxLifecycle` backed by your job/account journal. A private native
Claude account is another explicit binding; see the provider guide for serialization and isolation.
