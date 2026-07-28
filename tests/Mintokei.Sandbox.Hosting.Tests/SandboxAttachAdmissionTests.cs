using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Hosting;
using Xunit;

namespace Mintokei.Sandbox.Hosting.Tests;

/// <summary>
/// The gate a session passes to join an EXISTING sandbox. Every case here is the same question: could this
/// admit a session beside one that never agreed to share its broker, its allowlist and its credentials?
/// </summary>
public class SandboxAttachAdmissionTests
{
    private sealed class DeclaringRuntime(IReadOnlyList<string> tools) : FakeRuntime, ISandboxAdmissionSource
    {
        public Task<IReadOnlyList<string>> GetAdmittedToolsAsync(SandboxHandle handle, CancellationToken ct = default)
            => Task.FromResult(tools);
    }

    private static readonly SandboxHandle Handle = new("id", "sb-1", "docker");

    [Fact]
    public async Task Admits_a_session_whose_tool_the_sandbox_declares()
    {
        var p = Provisioner(new DeclaringRuntime(["ClaudeCodeCli"]));
        await p.EnsureCanAttachAsync(Handle, "ClaudeCodeCli");   // does not throw
    }

    [Fact]
    public async Task Refuses_a_tool_the_sandbox_was_not_built_for()
    {
        var p = Provisioner(new DeclaringRuntime(["ClaudeCodeCli"]));
        var ex = await Assert.ThrowsAsync<SandboxAdmissionException>(
            () => p.EnsureCanAttachAsync(Handle, "CodexCli"));
        Assert.Contains("CodexCli", ex.Message);
    }

    [Fact]
    public async Task Refuses_to_join_a_sandbox_that_carries_no_declaration()
    {
        // An undeclared sandbox is SINGLE-SESSION — provisioned before sharing, or by a caller that never
        // opted in. Elsewhere an empty declaration means "unconstrained"; here it must mean "do not join",
        // because the session already inside it never agreed to share its broker.
        var p = Provisioner(new DeclaringRuntime([]));
        var ex = await Assert.ThrowsAsync<SandboxAdmissionException>(
            () => p.EnsureCanAttachAsync(Handle, "ClaudeCodeCli"));
        Assert.Contains("single-session", ex.Message);
    }

    [Fact]
    public async Task Refuses_when_the_backend_cannot_report_a_declaration()
    {
        // No ISandboxAdmissionSource → nothing to check against. Sharing is unavailable on that backend rather
        // than assumed safe; provisioning a fresh sandbox still works.
        var p = Provisioner(new FakeRuntime());
        await Assert.ThrowsAsync<SandboxAdmissionException>(
            () => p.EnsureCanAttachAsync(Handle, "ClaudeCodeCli"));
    }

    private static SandboxProvisioner Provisioner(ISandboxRuntime runtime)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sandbox:BackendUrl"] = "https://backend/api",
            ["Sandbox:Backend"] = "docker",
        });
        builder.AddSandboxAgentHost().AddClaude();
        builder.Services.AddSingleton(runtime);
        return builder.Services.BuildServiceProvider().GetRequiredService<SandboxProvisioner>();
    }
}
