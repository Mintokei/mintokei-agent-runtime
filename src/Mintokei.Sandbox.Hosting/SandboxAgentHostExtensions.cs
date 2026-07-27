using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mintokei.Runner.Host.Hosting;

namespace Mintokei.Sandbox.Hosting;

/// <summary>
/// One-call registration for a host that runs agents in sandboxes. <c>AddSandboxAgentHost</c> composes the
/// transport half (<c>AddMintokeiRunnerHost</c>: SQLite + gRPC + JWT + enrollment + the control plane) with
/// the isolation half (<c>AddMintokeiSandbox</c>: runtimes + profiles + spec factory) and adds
/// <see cref="SandboxAgentHost"/> on top. <c>MapSandboxAgentHost</c> adds the matching pipeline.
/// </summary>
/// <example>
/// <code>
/// var builder = WebApplication.CreateBuilder(args);
/// builder.AddSandboxAgentHost().AddClaude();   // + .AddCodex() / .AddRemoteWorkers()
/// var app = builder.Build();
/// app.MapSandboxAgentHost();
/// </code>
/// Config: <c>Sandbox:Backend</c> picks docker vs kubernetes; <c>SandboxAgentHost:BackendUrl</c> is the URL
/// the sandbox dials back on. Everything else has a working default.
/// </example>
public static class SandboxAgentHostExtensions
{
    /// <summary>Register the full sandboxed-agent stack. Pass <paramref name="configureDb"/> to use your own
    /// database instead of the default SQLite.</summary>
    public static ISandboxAgentHostBuilder AddSandboxAgentHost(
        this WebApplicationBuilder builder, Action<DbContextOptionsBuilder>? configureDb = null)
    {
        // Transport: enrollment + control plane + gRPC data plane the sandbox's runner dials back into.
        var runnerHost = builder.AddMintokeiRunnerHost(configureDb);

        // Isolation: the runtime behind ISandboxRuntime is chosen by Sandbox:Backend (docker | kubernetes),
        // so the same calling code runs a container locally or a pod in a cluster.
        builder.Services.AddMintokeiSandbox(builder.Configuration);

        builder.Services.Configure<SandboxAgentHostOptions>(
            builder.Configuration.GetSection(SandboxAgentHostOptions.Section));
        builder.Services.TryAddSingleton<SandboxAgentHost>();

        return new SandboxAgentHostBuilder(runnerHost);
    }

    /// <summary>Register the Claude Code CLI backend.</summary>
    public static ISandboxAgentHostBuilder AddClaude(this ISandboxAgentHostBuilder builder)
    {
        builder.RunnerHost.AddClaude();
        return builder;
    }

    /// <summary>Register the Codex CLI backend.</summary>
    public static ISandboxAgentHostBuilder AddCodex(this ISandboxAgentHostBuilder builder)
    {
        builder.RunnerHost.AddCodex();
        return builder;
    }

    /// <summary>Also allow sandboxes to be launched ON CONNECTED WORKERS (nested Docker, dispatched over the
    /// worker's control channel) — needed when a run sets <see cref="SandboxAgentRequest.HostMachineId"/>.</summary>
    public static ISandboxAgentHostBuilder AddRemoteWorkers(this ISandboxAgentHostBuilder builder)
    {
        builder.Services.AddMintokeiRemoteSandbox();
        return builder;
    }

    /// <summary>Map the runner-facing pipeline (auth + enroll/token routes + the gRPC data plane) and ensure
    /// the schema exists — the sandbox's runner needs all of it to enroll and stream.</summary>
    public static WebApplication MapSandboxAgentHost(this WebApplication app) => app.MapMintokeiRunnerHost();
}

/// <summary>Fluent surface returned by <c>AddSandboxAgentHost</c>: register agent backends and opt into
/// remote workers. <see cref="RunnerHost"/> is the escape hatch to the underlying runner-host builder.</summary>
public interface ISandboxAgentHostBuilder
{
    /// <summary>The service collection being configured.</summary>
    IServiceCollection Services { get; }

    /// <summary>The composed runner-host builder, for registrations this facade doesn't re-expose.</summary>
    IMintokeiRunnerHostBuilder RunnerHost { get; }
}

internal sealed class SandboxAgentHostBuilder(IMintokeiRunnerHostBuilder runnerHost) : ISandboxAgentHostBuilder
{
    public IServiceCollection Services => runnerHost.Services;
    public IMintokeiRunnerHostBuilder RunnerHost { get; } = runnerHost;
}
