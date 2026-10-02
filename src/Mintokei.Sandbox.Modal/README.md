# Modal sandbox backend

`Mintokei.Sandbox.Modal` implements `ISandboxRuntime` using the official Modal Python SDK.
It launches the standard Mintokei runner; enrollment, command dispatch, streaming and reconnects use
the existing runner protocol. There is no application database, user model, billing or task state here.
Experimental source project; it is not published to NuGet.

Start with the buildable [ModalAgentMinimal sample](../../samples/ModalAgentMinimal).

## Compose the existing layers

```csharp
var client = new ModalClient(new ModalClientOptions
{
    AppName = "my-agents", OwnerId = executionTargetId,
    Environment = "main", Bundle = "/srv/modal/bundle"
}, ct => secrets.ResolveModalCredentialsAsync(executionTargetId, ct));

var runtime = new ModalSandboxRuntime(client,
    new ModalSandboxPolicy(["runner.example.com", "models.example.com"]), launchJournal);

await using var run = await host.RunAsync(new SandboxAgentRequest
{
    Runtime = runtime, Profile = "modal", Prompt = "Your task",
    Session = new AgentSessionSpec
    {
        Tool = AgentToolKey.ClaudeCodeCli,
        WorkingDirectory = "/workspace",
        Config = new() { ["model"] = configuredModel },
        EnvironmentVariables = authorizedRunEnvironment
    }
}, ct);
await foreach (var output in run.Output.WithCancellation(ct)) { /* host consumes events */ }
```

The names `secrets`, `launchJournal` and `authorizedRunEnvironment` above are host responsibilities.
Register the existing `AddSandboxAgentHost().AddClaude()` / `.AddCodex()` stack, or compose the lower
layers directly. Set a `runsc` profile with `Egress=Open`; Modal's domain policy is a separate provider
capability. `SandboxAgentRequest.Runtime` selects an explicit target per request without replacing a
global singleton or sharing another target's warm pool. `Session` carries the complete engine spec.
Use one fresh sandbox per inference operation; multi-session admission and repository seeding are not
implemented by this backend.

The default helper expects a self-contained Linux x64 runner at `Bundle/runner/Mintokei.Runner`.
The project copies `modal_runtime.py` and `modal_native_claude.py` to `sandbox/` on build and publish.
Install `python/requirements.txt` on the coordinator. `ModalClientOptions.Python` can select a venv.
Credentials are resolved on the coordinator and passed over stdin, never CLI arguments or sandbox
environment. The helper has an explicit account/environment and ignores ambient Modal profiles.
An SDK operation timeout is an **unknown outcome**, not proof that no sandbox was created.

## Capabilities and lifetime

| Input | Modal behavior |
| --- | --- |
| CPU and memory request/limit | Both mapped; CPU uses physical cores = runtime vCPU / 2; memory rounded up to MiB |
| Image | Managed image built from the runner bundle; `SandboxSpec.Image` is not a registry-image override |
| Isolation | Modal's gVisor sandbox with pinned SDK 1.5.5; `runsc` is the accepted profile |
| Workspace | Fresh `/workspace`; `/data` is ephemeral sandbox storage, not a Linux tmpfs mount |
| PIDs limit | Docker-only field; no per-sandbox PIDs limit applied by this backend |
| Network | Null policy permits outbound access; explicit domains allow TLS/443 and set CIDR allowlist to empty |
| Native CLI auth | Opaque private Modal volume; optional controller/workspace separation |
| Host mounts, repo seeding, persistent workspace, read-only root, Docker broker/proxy, tool admission | Rejected before launch |

Domain restrictions have no permissive fallback if the SDK rejects them. They are not the runtime's
Docker CONNECT broker. Include enrollment, gRPC, the authorized model endpoint and any actual tool
endpoints. A domain policy does not enforce model names, URL paths or per-run authorization; the
application's model gateway owns those decisions.

Implement `IModalSandboxLifecycle` for durable operation:

1. `BeforeLaunchAsync`: persist the unique name and target, and acquire any account/capacity lease.
2. `LaunchedAsync`: persist the provider ID **before** interpreting network/isolation confirmation.
3. On failure or restart, reconcile that same name/ID and target; do not issue a new launch while the
   original remains uncertain. The runtime lists runner sandboxes in the explicitly bound app.
4. Release the host's lease only after termination is confirmed. `StopAsync` throws for a name-only
   not-found result because a timed-out create might still complete. Known-ID not-found is terminal.

`SandboxAgentRun` attempts cleanup on disposal and allows retry after a failed disposal. In-memory
tracking is not a durable reconciler. A process crash or lost launch acknowledgement still needs the
host's journal/reaper; use Modal's timeout as an additional lifetime bound.

## Authentication is separate from compute

**API or an authorized model gateway:** supply the CLI's documented environment in `AgentSessionSpec`.
For a backend-held API key, give the sandbox only a scoped gateway token. This package does not install
a universal subscription-to-API proxy, route arbitrary account tokens, or choose an account.

**Claude native CLI account:** `ModalNativeClaude` exposes start/status/open/close/delete operations.
Persist `ModalNativeLogin` before `StartAsync`, save the returned sandbox ID, and keep the sign-in URL
restricted to the account owner. Status returns readiness, not provider credentials. The official CLI
owns sign-in and refresh in its private Volume v2 home; shutdown syncs the home before termination.
Use the same stable app, environment, owner and `ResourcePrefix` for login, execution and deletion.
The host must serialize use of one account/home, confirm login/execution sandboxes have stopped, then
delete the home. These methods do not implement locking or user consent.

For unattended inference, use `ModalSandboxPolicy(domains, native.Account(id), IsolateWorkspace: true)`
with `WorkingDirectory="/control"` and `ClaudeSessionProfiles.Inference`. The controller runs as UID
10001, its auth volume is below `/native-private`, and `/workspace` belongs to UID 10002. The CLI's
built-in tools, MCP, hooks and slash commands are disabled by the inference profile. Native tools need
an application-authorized bridge running under the workspace identity; the cloud provider alone does
not add that bridge. Ordinary interactive native runs may use the non-isolated mode, where the CLI's
tools share its user and credential home.

**Codex account sign-in:** the separate [AgentAuthentication project](../Mintokei.AgentAuthentication)
drives official Codex app-server login/refresh. The host owns encrypted credential storage, refresh
serialization, routing and authorized usage. Using a native account is an explicit host choice; API
authentication and native subscription authentication are not interchangeable provider contracts.

## JSON inference

`IAgentJsonExecutor` / `SandboxJsonExecutor` compose a fresh `SandboxAgentHost` run with
`ClaudeSessionProfiles.Inference` and `AgentJsonOutput.CollectAsync`. They return the bounded final
assistant JSON only after a successful turn boundary. Tool/reasoning text is excluded, interactions
are denied, and invalid JSON, provider failures, interruptions and incomplete streams throw. Callers
apply their own schema/domain validation and own retries/checkpoints. Supply a cancellation deadline.

The inference-only profile currently supports **Claude Code**. Normal engine sessions still support
Codex; Codex inference needs its own verified tool-disable mapping before this executor accepts it.
CLI arguments preserve literal empty values over remote dispatch (`--tools ""`, `--setting-sources ""`).
Modal runners disable background model discovery so account probing cannot race native use.

## Verification

```sh
dotnet test tests/Mintokei.Sandbox.Modal.Tests
dotnet test tests/Mintokei.Sandbox.Hosting.Tests
python3 -m unittest discover -s tests/modal -v
```

These are offline contract/lifecycle tests. A real-account smoke test is separate and incurs cloud/model
usage. Provider behavior and operational limits are documented in the official
[Modal Sandbox reference](https://modal.com/docs/reference/modal.Sandbox).
