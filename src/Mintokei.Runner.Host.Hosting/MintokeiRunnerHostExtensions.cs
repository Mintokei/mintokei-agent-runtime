using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.Codex;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.Runner.Host.Persistence;
using Mintokei.Runner.Host.RemoteExecution;
using Mintokei.Runner.Host.RemoteExecution.Grpc;
using Mintokei.Runner.Host.Server;

namespace Mintokei.Runner.Host.Hosting;

/// <summary>
/// One-call registration for a complete remote-runner backend. <c>AddMintokeiRunnerHost</c> composes the
/// pieces a host would otherwise wire by hand — throwaway/opt-in SQLite, the transport core, a no-op
/// recovery default, enrollment + JWT auth, the control plane, and gRPC — all driven by the <c>RunnerHost</c>
/// config section. <c>MapMintokeiRunnerHost</c> adds the matching pipeline (auth + the enroll/token routes +
/// the gRPC data plane). Every registration uses <c>TryAdd</c>, so registering your own
/// <see cref="IRemoteProcessRecovery"/>, extra <see cref="IAgentBackend"/>s, or a custom DbContext still wins.
/// The granular <c>AddRunnerHostCore</c>/<c>AddRunnerHostServer</c>/… methods stay public for hand-wiring.
/// </summary>
public static class MintokeiRunnerHostExtensions
{
    private const string InMemoryConnection = "Data Source=MintokeiRunnerHost;Mode=Memory;Cache=Shared";

    /// <summary>Register a full runner-host backend from the <c>RunnerHost</c> config section. Pass
    /// <paramref name="configureDb"/> to use a non-SQLite provider (overrides the connection-string default).</summary>
    public static IMintokeiRunnerHostBuilder AddMintokeiRunnerHost(
        this WebApplicationBuilder builder, Action<DbContextOptionsBuilder>? configureDb = null)
    {
        var services = builder.Services;
        var section = builder.Configuration.GetSection(MintokeiRunnerHostOptions.Section);
        var options = section.Get<MintokeiRunnerHostOptions>() ?? new MintokeiRunnerHostOptions();
        services.Configure<MintokeiRunnerHostOptions>(section); // so MapMintokeiRunnerHost can read ApiPrefix

        // Persistence: caller-supplied provider → configured SQLite → throwaway in-memory (kept alive for
        // the process lifetime). TryAdd so a host that registered its own RunnerHostDbContext wins.
        if (configureDb is not null)
        {
            services.AddDbContext<RunnerHostDbContext>(configureDb);
        }
        else if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            services.AddDbContext<RunnerHostDbContext>(o => o.UseSqlite(options.ConnectionString));
        }
        else
        {
            var keepAlive = new SqliteConnection(InMemoryConnection);
            keepAlive.Open();
            services.AddSingleton(keepAlive); // lifetime anchor; DI disposes it on shutdown
            services.AddDbContext<RunnerHostDbContext>(o => o.UseSqlite(InMemoryConnection));
        }

        // Transport core + local command runner + a no-op recovery default.
        services.AddRunnerHostCore();
        services.TryAddSingleton<ICommandLineRunner, CommandLineRunner>();
        services.TryAddSingleton<IRemoteProcessRecovery, NoOpRemoteProcessRecovery>();

        // Enrollment + JWT auth (validates the machine_id claim on the gRPC data plane).
        var signingKey = string.IsNullOrWhiteSpace(options.SigningKey)
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) // dev: ephemeral key
            : options.SigningKey;
        services.AddRunnerHostServer(o =>
        {
            o.SigningKey = signingKey;
            o.Issuer = options.Issuer;
            o.Audience = options.Audience;
        });
        services.AddAuthentication().AddJwtBearer("RunnerJwt", o =>
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = options.Issuer,
                ValidateAudience = true, ValidAudience = options.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
                ClockSkew = TimeSpan.FromSeconds(30),
            });
        services.AddAuthorization(o =>
            o.AddPolicy("Runner", p => p.AddAuthenticationSchemes("RunnerJwt")
                                        .RequireAuthenticatedUser()
                                        .RequireClaim("machine_id")));

        // Control plane + gRPC transport.
        services.AddAgentControlPlane();
        services.AddGrpc();
        services.AddScoped<RunnerLinkService>();

        var runnerBuilder = new MintokeiRunnerHostBuilder(services);
        foreach (var backend in options.AgentBackends) // config-driven backends (dedupes with the fluent calls)
            runnerBuilder.AddBackend(backend);
        return runnerBuilder;
    }

    /// <summary>Map the runner-facing pipeline: authentication + the enroll/token routes (under
    /// <c>RunnerHost:ApiPrefix</c>) + the authorized gRPC data plane, and ensure the DB schema exists.</summary>
    public static WebApplication MapMintokeiRunnerHost(this WebApplication app)
    {
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>().Database.EnsureCreated();

        var options = app.Services.GetRequiredService<IOptions<MintokeiRunnerHostOptions>>().Value;

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGroup(options.ApiPrefix).MapRunnerHost();                        // enroll / token exchange
        app.MapGrpcService<RunnerLinkService>().RequireAuthorization("Runner"); // gRPC data plane
        return app;
    }

    /// <summary>Register the Claude Code CLI backend.</summary>
    public static IMintokeiRunnerHostBuilder AddClaude(this IMintokeiRunnerHostBuilder builder)
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentBackend, ClaudeBackend>());
        return builder;
    }

    /// <summary>Register the Codex CLI backend.</summary>
    public static IMintokeiRunnerHostBuilder AddCodex(this IMintokeiRunnerHostBuilder builder)
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentBackend, CodexBackend>());
        return builder;
    }
}

internal sealed class MintokeiRunnerHostBuilder(IServiceCollection services) : IMintokeiRunnerHostBuilder
{
    public IServiceCollection Services { get; } = services;

    public void AddBackend(string name) => _ = name.Trim().ToLowerInvariant() switch
    {
        "claude" => this.AddClaude(),
        "codex" => this.AddCodex(),
        _ => throw new InvalidOperationException(
            $"Unknown RunnerHost:AgentBackends entry '{name}'. Known values: Claude, Codex."),
    };
}

/// <summary>Default <see cref="IRemoteProcessRecovery"/>: nothing is kept across a host restart, so a lost
/// correlation simply ends its session. Register your own before/after <c>AddMintokeiRunnerHost</c> to override.</summary>
internal sealed class NoOpRemoteProcessRecovery : IRemoteProcessRecovery
{
    public Task<RemoteProcessHandle?> TryRecoverAsync(Guid correlationId, Guid machineId) =>
        Task.FromResult<RemoteProcessHandle?>(null);
}
