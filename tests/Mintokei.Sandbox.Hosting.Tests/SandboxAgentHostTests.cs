using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.Runner.Host.Server;
using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Hosting.Tests;

public class SandboxAgentHostTests
{
    private static (SandboxAgentHost Host, FakeRuntime Runtime, FakeEnrollment Enrollment, FakeControlPlane Plane)
        NewHost(Action<SandboxAgentHostOptions>? configureHost = null, Action<SandboxOptions>? configureSandbox = null)
    {
        var sandboxOptions = new SandboxOptions
        {
            Image = "img:1",
            AllowedProfiles = ["standard"],
            Profiles = { ["standard"] = new SandboxProfileConfig() },
        };
        configureSandbox?.Invoke(sandboxOptions);
        var sandbox = Options.Create(sandboxOptions);

        var hostOptions = new SandboxAgentHostOptions { BackendUrl = "https://backend/api" };
        configureHost?.Invoke(hostOptions);

        var runtime = new FakeRuntime();
        var manager = new SandboxManager(
            runtime, new SandboxProfileResolver(sandbox), new SandboxSpecFactory(sandbox), sandbox,
            NullLogger<SandboxManager>.Instance, new NoBrokerSecrets());
        var enrollment = new FakeEnrollment();
        var plane = new FakeControlPlane();

        // IRunnerEnrollment is scoped in the real container, so the provisioner takes it through a scope
        // factory rather than capturing it — mirror that here.
        var services = new ServiceCollection()
            .AddScoped<IRunnerEnrollment>(_ => enrollment)
            .BuildServiceProvider();

        var provisioner = new SandboxProvisioner(
            services.GetRequiredService<IServiceScopeFactory>(), manager, runtime, plane,
            Options.Create(hostOptions), NullLogger<SandboxProvisioner>.Instance, services);
        var host = new SandboxAgentHost(provisioner, plane, NullLogger<SandboxAgentHost>.Instance);

        return (host, runtime, enrollment, plane);
    }

    private static SandboxAgentRequest Request(string? repo = null, string? prompt = null) =>
        new() { Tool = AgentToolKey.ClaudeCodeCli, Repo = repo, Prompt = prompt };

    [Fact]
    public async Task RunAsync_mints_an_ephemeral_identity_and_binds_the_session_to_it()
    {
        var (host, runtime, enrollment, plane) = NewHost();

        await using var run = await host.RunAsync(Request(prompt: "hi"));

        // The machine identity is pre-created (ephemeral, named like the container) and the session is
        // dispatched to THAT id — never discovered by name after the fact.
        Assert.True(enrollment.RequestedEphemeral);
        Assert.Equal("standard", enrollment.RequestedProfile);
        Assert.Equal(enrollment.MachineId, run.MachineId);
        Assert.Equal(enrollment.MachineId, plane.StartedOnMachine);
        Assert.Equal(enrollment.RequestedMachineName, run.SandboxName);

        // The sandbox was launched with the minted token, and the prompt was sent once ready.
        var spec = Assert.Single(runtime.Provisioned);
        Assert.Equal(run.SandboxName, spec.Name);
        Assert.Contains(enrollment.Token, spec.Args);
        Assert.Equal(["hi"], plane.Session.Sent);
    }

    [Fact]
    public async Task RunAsync_defaults_the_working_directory_to_the_first_repo_checkout()
    {
        var (host, _, _, plane) = NewHost();

        await using var run = await host.RunAsync(Request(repo: "https://github.com/acme/app.git"));

        Assert.Equal("/repos/app", plane.StartedSpec!.WorkingDirectory);
    }

    [Fact]
    public async Task RunAsync_without_a_repo_starts_the_session_at_the_repo_root()
    {
        var (host, _, _, plane) = NewHost();

        await using var run = await host.RunAsync(Request());

        Assert.Equal(SandboxSpecFactory.RepoRoot, plane.StartedSpec!.WorkingDirectory);
    }

    [Fact]
    public async Task RunAsync_seeds_the_host_wide_credentials_by_default()
    {
        var (host, runtime, _, _) = NewHost(o => o.ClaudeConfigHostDir = "/host/.claude");

        await using var run = await host.RunAsync(Request());

        var spec = Assert.Single(runtime.Provisioned);
        Assert.Contains(spec.Mounts, m => m.Source == "/host/.claude");
    }

    [Fact]
    public async Task Configure_hook_overrides_host_wide_credentials_per_run()
    {
        // The point of the hook: credentials are per-tenant in a real product, not one host-wide path.
        var (host, runtime, _, _) = NewHost(o => o.ClaudeConfigHostDir = "/host/.claude");

        await using var run = await host.RunAsync(
            Request(),
            r => r with { ClaudeConfigHostDir = "/tenants/acme/.claude" });

        var spec = Assert.Single(runtime.Provisioned);
        Assert.Contains(spec.Mounts, m => m.Source == "/tenants/acme/.claude");
        Assert.DoesNotContain(spec.Mounts, m => m.Source == "/host/.claude");
    }

    [Fact]
    public async Task Configure_hook_supplies_broker_egress_needs()
    {
        // Broker egress fails closed without a per-session allowlist, so this is only expressible through the
        // hook — it is what the product (which runs broker egress in production) needs from the facade.
        var (host, runtime, _, _) = NewHost(
            configureHost: o => o.AddHostGateway = true, // host-wide dev default…
            configureSandbox: s => s.Profiles["standard"] = new SandboxProfileConfig { Egress = "broker" });

        await using var run = await host.RunAsync(
            Request(),
            r => r with
            {
                Broker = new SandboxBrokerNeeds(["anthropic"], Git: true, Allowlist: ["api.anthropic.com"]),
                AddHostGateway = false, // …turned off for this run, as broker containment requires
            });

        var spec = Assert.Single(runtime.Provisioned);
        Assert.Equal(SandboxEgress.Broker, spec.Egress);
        Assert.Equal(["api.anthropic.com"], spec.EgressAllowlist);
        Assert.False(spec.AddHostGateway);
    }

    [Fact]
    public async Task Broker_egress_is_a_first_class_request_input_and_switches_to_the_public_url()
    {
        // Broker egress only tunnels TLS, so a brokered sandbox can't dial a plaintext in-cluster URL — the
        // switch to the public ingress is a property of broker egress, so the library makes it, not the caller.
        var (host, runtime, _, _) = NewHost(
            configureHost: o =>
            {
                o.BackendUrl = "http://api.internal:8080";          // in-cluster, unreachable through the proxy
                o.GrpcBackendUrl = "http://api.internal:8081";
                o.PublicBackendUrl = "https://mintokei.example";    // …so brokered runs use these instead
                o.PublicGrpcBackendUrl = "https://mintokei.example";
            },
            configureSandbox: s => s.Profiles["standard"] = new SandboxProfileConfig { Egress = "broker" });

        await using var run = await host.RunAsync(Request() with
        {
            Broker = new SandboxBrokerNeeds(["anthropic"], Git: true, Allowlist: ["api.anthropic.com"]),
        });

        var spec = Assert.Single(runtime.Provisioned);
        Assert.Equal(SandboxEgress.Broker, spec.Egress);
        Assert.Equal(["api.anthropic.com"], spec.EgressAllowlist);       // the per-session allowlist reached the spec
        Assert.Contains("https://mintokei.example", spec.Args);          // …dialing the PUBLIC url
        Assert.DoesNotContain("http://api.internal:8080", spec.Args);
    }

    [Fact]
    public async Task Non_broker_sessions_keep_the_in_cluster_url()
    {
        var (host, runtime, _, _) = NewHost(o =>
        {
            o.BackendUrl = "http://api.internal:8080";
            o.PublicBackendUrl = "https://mintokei.example";
        });

        await using var run = await host.RunAsync(Request()); // no Broker → no public swap

        var spec = Assert.Single(runtime.Provisioned);
        Assert.Contains("http://api.internal:8080", spec.Args);
    }

    [Fact]
    public async Task Configure_hook_cannot_break_the_identity_the_run_is_bound_to()
    {
        var (host, runtime, enrollment, _) = NewHost();

        await using var run = await host.RunAsync(
            Request(),
            r => r with { Name = "hijacked", EnrollmentToken = "not-the-minted-one" });

        // Name + token are re-pinned: they are identity, not policy. Everything else the hook sets stands.
        var spec = Assert.Single(runtime.Provisioned);
        Assert.NotEqual("hijacked", spec.Name);
        Assert.Equal(run.SandboxName, spec.Name);
        Assert.Contains(enrollment.Token, spec.Args);
        Assert.DoesNotContain("not-the-minted-one", spec.Args);
    }

    [Fact]
    public async Task RunAsync_recycles_the_sandbox_and_surfaces_its_logs_when_it_exits_during_startup()
    {
        var (host, runtime, _, plane) = NewHost();
        plane.Connected = false;              // the runner never dials back…
        runtime.Status = SandboxState.Exited; // …because the container died during startup
        runtime.ExitCode = 1;
        runtime.Logs = "fatal: could not read Username for 'https://github.com'";

        var ex = await Assert.ThrowsAsync<SandboxAgentException>(() => host.RunAsync(Request()));

        // "Exited" is a different diagnosis from "timed out" — say so, and lead with the exit code.
        Assert.Contains("exited (exit code 1)", ex.Message);
        Assert.Equal(SandboxState.Exited, ex.TerminalState);
        Assert.Equal(1, ex.ExitCode);
        Assert.Contains("could not read Username", ex.ContainerLogs);
        Assert.Single(runtime.Stopped); // recycled rather than leaked
    }

    [Fact]
    public async Task RunAsync_reports_a_timeout_differently_from_a_container_that_died()
    {
        var (host, runtime, _, plane) = NewHost(o => o.OnDemandTimeoutSeconds = 1);
        plane.Connected = false;               // never dials back…
        runtime.Status = SandboxState.Running; // …but the container is alive, so this is a timeout

        var ex = await Assert.ThrowsAsync<SandboxAgentException>(() => host.RunAsync(Request()));

        Assert.Contains("did not become ready", ex.Message);
        Assert.Null(ex.TerminalState); // it never exited
        Assert.Single(runtime.Stopped);
    }

    [Fact]
    public async Task RunAsync_recycles_the_sandbox_when_the_session_cannot_start()
    {
        var (host, runtime, _, plane) = NewHost();
        plane.StartThrows = new InvalidOperationException("no backend registered for ClaudeCodeCli");

        var ex = await Assert.ThrowsAsync<SandboxAgentException>(() => host.RunAsync(Request()));

        Assert.Contains("session could not start", ex.Message);
        Assert.Single(runtime.Stopped);
    }

    [Fact]
    public async Task Disposing_the_run_stops_the_session_and_recycles_the_sandbox()
    {
        var (host, runtime, _, plane) = NewHost();

        var run = await host.RunAsync(Request());
        Assert.Empty(runtime.Stopped);

        await run.DisposeAsync();

        Assert.Equal([plane.Session.SessionId], plane.Stopped);
        Assert.Single(runtime.Stopped);

        await run.DisposeAsync(); // idempotent — safe from a finally
        Assert.Single(runtime.Stopped);
    }

    [Fact]
    public async Task RunAsync_registers_the_session_under_a_caller_supplied_key()
    {
        var (host, _, _, plane) = NewHost();
        var key = Guid.NewGuid();

        var run = await host.RunAsync(Request() with { SessionKey = key });
        await run.DisposeAsync();

        Assert.Equal([key], plane.Stopped); // stopped by the caller's key, not the session's own id
    }

    [Fact]
    public async Task RunAsync_fails_fast_when_no_backend_url_is_configured()
    {
        var (host, runtime, _, _) = NewHost(o => o.BackendUrl = null);

        var ex = await Assert.ThrowsAsync<SandboxAgentException>(() => host.RunAsync(Request()));

        Assert.Contains("BackendUrl", ex.Message);
        Assert.Empty(runtime.Provisioned); // nothing was launched
    }
}
