# CLI account authentication

Small, database-free helpers for official CLI authentication. Experimental source project, not yet
published to NuGet. Applications own account authorization, storage encryption, concurrent refresh
locks, reconnect state and the choice of which account may run a workload.

```csharp
ICodexAccountClient client = new CodexAccountClient(new CodexAccountOptions("/usr/local/bin/codex"));
var result = await client.Login(challenge => ShowToAccountOwner(challenge.Url, challenge.Code), ct);
// Encrypt result.AuthJson in host-owned storage; never return it to the browser or write it to logs.
var updatedAuth = await client.Refresh(decryptedAuthJson, ct);
```

This uses official Codex app-server `account/login/start` device-code sign-in, account readiness and
model listing. Refresh runs `account/read` with refresh enabled. It never starts an agent thread or
model request. Every operation has a temporary private HOME/CODEX_HOME, cleared process environment,
file credential storage and cleanup on completion/cancellation. Supply an operation deadline through
the cancellation token. Public credential/challenge records redact `ToString`; the host must still
avoid logging or serializing their secret properties.

`CodexCredential.Parse` extracts an account ID/token and JWT expiry as a refresh hint. It does **not**
verify a JWT or authorize model use; the provider authenticates the actual token. This module provides
neither a model gateway nor a general API-key replacement. See the provider's
[Codex authentication documentation](https://developers.openai.com/codex/auth).

Claude native sign-in depends on the execution environment and lives in
[`Mintokei.Sandbox.Modal`](../Mintokei.Sandbox.Modal): its official CLI owns credentials inside a private
Modal volume. No Claude subscription token is extracted or returned by this helper.
