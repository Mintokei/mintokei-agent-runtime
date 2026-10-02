using System.Text.Json;

namespace Mintokei.Sandbox.Modal;

/// <summary>Opaque private CLI home. This is authentication storage, not a workspace or transcript.</summary>
public sealed record ModalNativeAccount(Guid AccountId, string Home);

/// <summary>Persist this intent before starting sign-in. Preserve SandboxId after acknowledgement;
/// unknown creates must reconcile by name rather than launching another sign-in.</summary>
public sealed record ModalNativeLogin(Guid AccountId, Guid LoginId, DateTimeOffset ExpiresAt, string? SandboxId = null);

/// <summary>Official Claude CLI sign-in on Modal. Hosts own authorization, serialization of account use,
/// durable login state, reconnect UX and deletion after every active sandbox has stopped.</summary>
public sealed class ModalNativeClaude(IModalClient client, string resourcePrefix = "mintokei")
{
    public ModalNativeAccount Account(Guid id) => new(id, $"{resourcePrefix}-native-{id:N}");
    public Task<JsonElement> StartAsync(ModalNativeLogin login, CancellationToken ct = default) => Call("native_start", login, ct);
    public Task<JsonElement> StatusAsync(ModalNativeLogin login, CancellationToken ct = default) => Call("native_status", login, ct);
    public Task<JsonElement> OpenAsync(ModalNativeLogin login, CancellationToken ct = default) => Call("native_open", login, ct);
    public Task<JsonElement> CloseAsync(ModalNativeLogin login, CancellationToken ct = default) => Call("native_close", login, ct);
    public Task<JsonElement> DeleteHomeAsync(Guid accountId, CancellationToken ct = default)
        => client.CallAsync("native_delete", Account(accountId), ct);
    private Task<JsonElement> Call(string operation, ModalNativeLogin login, CancellationToken ct)
        => client.CallAsync(operation, new
        {
            accountId = login.AccountId, home = Account(login.AccountId).Home,
            name = $"{resourcePrefix}-login-{login.LoginId:N}", id = login.SandboxId,
            expiresAt = login.ExpiresAt.ToUnixTimeSeconds()
        }, ct);
}
