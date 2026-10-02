using System.Text.Json;
using Xunit;

namespace Mintokei.Sandbox.Modal.Tests;

public sealed class ModalRuntimeTests
{
    [Fact]
    public async Task Running_status_accepts_null_exit_code()
    {
        var runtime = new ModalSandboxRuntime(new Client((_, _) => "{\"state\":\"running\",\"exitCode\":null}"));
        var status = await runtime.GetStatusAsync(new("sb-real", "session-a", "modal"));
        Assert.Equal(SandboxState.Running, status.State); Assert.Null(status.ExitCode);
    }
    private static SandboxSpec Spec() => new()
    {
        Image = "managed", Name = "session-a", RuntimeClass = "runsc", Limits = new(4096L * 1024 * 1024, 2, 512),
        Env = new Dictionary<string, string> { ["Runner__GrpcBackendUrl"] = "https://runner.example.test" }
    };

    [Fact]
    public async Task Intent_precedes_cloud_call_and_id_is_saved_before_policy_failure()
    {
        var events = new List<string>();
        var client = new Client((op, payload) => { events.Add(op); return "{\"id\":\"sb-real\",\"networkRestricted\":false}"; });
        var observer = new Observer(events);
        var runtime = new ModalSandboxRuntime(client, new(["api.example.test"]), observer);
        await Assert.ThrowsAsync<SandboxRuntimeException>(() => runtime.ProvisionAsync(Spec()));
        Assert.Equal(new[] { "intent", "launch", "saved:sb-real" }, events);
        Assert.Equal(2, client.Payload.GetProperty("spec").GetProperty("limits").GetProperty("cpuLimit").GetDouble());
    }

    [Fact]
    public async Task Uncertain_name_only_launch_stays_fenced_but_known_missing_id_is_stopped()
    {
        var client = new Client((_, _) => "{\"state\":\"not_found\"}");
        var runtime = new ModalSandboxRuntime(client);
        await Assert.ThrowsAsync<SandboxRuntimeException>(() => runtime.StopAsync(new("session-a", "session-a", "modal")));
        Assert.Equal(JsonValueKind.Null, client.Payload.GetProperty("id").ValueKind);
        await runtime.StopAsync(new("sb-real", "session-a", "modal"));
        Assert.Equal("sb-real", client.Payload.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Unsupported_capabilities_fail_before_persisting_or_spending()
    {
        var events = new List<string>();
        var client = new Client((_, _) => throw new Exception("must not call cloud"));
        var runtime = new ModalSandboxRuntime(client, lifecycle: new Observer(events));
        foreach (var spec in new[]
        {
            Spec() with { Mounts = [new("/host", "/seed", true)] },
            Spec() with { RuntimeClass = "runc" },
            Spec() with { Env = new Dictionary<string, string> { ["SANDBOX_REPOS"] = "repo" } },
            Spec() with { Egress = SandboxEgress.Broker },
            Spec() with { AdmittedTools = ["claude"] }
        }) await Assert.ThrowsAsync<SandboxRuntimeException>(() => runtime.ProvisionAsync(spec));
        Assert.Empty(events);
    }

    [Fact]
    public async Task Separate_clients_keep_targets_and_inventory_independent()
    {
        var a = new ModalSandboxRuntime(new Client((_, _) => "{\"sandboxes\":[{\"id\":\"sb-a\",\"name\":\"a\"}]}"));
        var b = new ModalSandboxRuntime(new Client((_, _) => "{\"sandboxes\":[{\"id\":\"sb-b\",\"name\":\"b\"}]}"));
        Assert.Equal("sb-a", Assert.Single(await a.ListManagedAsync()).Id);
        Assert.Equal("sb-b", Assert.Single(await b.ListManagedAsync()).Id);
    }

    [Fact]
    public async Task Native_isolation_requires_network_and_persists_missing_confirmation()
    {
        var events = new List<string>();
        var account = new ModalNativeAccount(Guid.NewGuid(), "opaque-home");
        var client = new Client((_, _) => "{\"id\":\"sb-real\",\"networkRestricted\":true}");
        var invalid = new ModalSandboxRuntime(client, new(null, account, true), new Observer(events));
        await Assert.ThrowsAsync<SandboxRuntimeException>(() => invalid.ProvisionAsync(Spec()));
        Assert.Empty(events);
        var valid = new ModalSandboxRuntime(client, new(["api.anthropic.com"], account, true), new Observer(events));
        await Assert.ThrowsAsync<SandboxRuntimeException>(() => valid.ProvisionAsync(Spec()));
        Assert.Contains("saved:sb-real", events);
    }

    private sealed class Observer(List<string> events) : IModalSandboxLifecycle
    {
        public Task BeforeLaunchAsync(SandboxSpec spec, CancellationToken ct) { events.Add("intent"); return Task.CompletedTask; }
        public Task LaunchedAsync(ModalLaunchResult result, CancellationToken ct) { events.Add("saved:" + result.Handle.Id); return Task.CompletedTask; }
    }
    private sealed class Client(Func<string, object, string> response) : IModalClient
    {
        public JsonElement Payload { get; private set; }
        public Task<JsonElement> CallAsync(string operation, object payload, CancellationToken ct = default)
        {
            Payload = JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(JsonDocument.Parse(response(operation, payload)).RootElement.Clone());
        }
    }
}
