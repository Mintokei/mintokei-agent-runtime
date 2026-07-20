using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.Runner.Host.Persistence;
using Mintokei.Runner.Host.RemoteExecution;
using Mintokei.Runner.Host.RemoteExecution.Grpc;
using Mintokei.Runner.Host.Server;

namespace SandboxRunnerHostMinimal;

/// <summary>
/// Demo-tunable settings, bound from the <c>Sandbox</c> configuration section (appsettings, environment
/// variables like <c>Sandbox__BackendUrl</c>, CLI args — the standard config providers). Defaults target a
/// local Docker host reaching this process via <c>host.docker.internal</c>. Credentials are optional; set
/// them to authenticate the in-container CLI so the agent turn can actually run.
/// </summary>
public sealed class SandboxDemoOptions
{
    public const string Section = "Sandbox";

    /// <summary>REST enroll URL the container's runner dials — must be reachable from inside the container.</summary>
    public string BackendUrl { get; set; } = "http://host.docker.internal:5082";

    /// <summary>gRPC control-stream URL the container's runner dials.</summary>
    public string GrpcBackendUrl { get; set; } = "http://host.docker.internal:5083";

    // Optional credential seeding: each host path is mounted RO at /seed and copied into the container's
    // HOME by the entrypoint. Unset → plumbing-only (the runner enrolls and the session dispatches, but
    // the CLI has no credentials to complete a turn).
    public string? ClaudeConfigHostDir { get; set; }
    public string? ClaudeConfigJsonHostFile { get; set; }
    public string? CodexConfigHostDir { get; set; }
    public string? GitCredentialsHostDir { get; set; }
}

/// <summary>
/// The generic backend plumbing for the sandbox demo, factored out of <c>Program.cs</c> so the sample reads
/// as "add the backend, then the one lifecycle endpoint". It hosts the real <c>Mintokei.Runner.Host</c>
/// (throwaway in-memory SQLite, JWT auth, gRPC data plane) + <c>AgentControlPlane</c> + <c>Mintokei.Sandbox</c>.
/// For a focused tour of the host wiring by itself, see the <c>RemoteRunnerMinimal</c> sample.
/// </summary>
public static class SandboxDemoBackend
{
    private const string DbConnectionString = "Data Source=SandboxRunnerHostMinimal;Mode=Memory;Cache=Shared";

    public static WebApplicationBuilder AddSandboxDemoBackend(this WebApplicationBuilder builder)
    {
        // Demo URLs + optional creds, from the "Sandbox" section (appsettings / env / args).
        builder.Services.Configure<SandboxDemoOptions>(builder.Configuration.GetSection(SandboxDemoOptions.Section));

        // A random HMAC key for the runner JWTs, shared between minting and validation (regenerated each
        // run — a real host loads it from secret storage so tokens survive a restart).
        var signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        // Throwaway in-memory SQLite for the runner-infra tables; one open connection keeps the shared-cache
        // DB alive for the process lifetime (registered so DI disposes it on shutdown).
        var keepAlive = new SqliteConnection(DbConnectionString);
        keepAlive.Open();
        builder.Services.AddSingleton(keepAlive);
        builder.Services.AddDbContext<RunnerHostDbContext>(o => o.UseSqlite(DbConnectionString));

        // Transport core + local command runner + the one no-op recovery seam.
        builder.Services.AddRunnerHostCore();
        builder.Services.AddSingleton<ICommandLineRunner, CommandLineRunner>();
        builder.Services.AddSingleton<IRemoteProcessRecovery, NoOpRemoteProcessRecovery>();

        // Runner enrollment + JWT auth (validates the machine_id claim on the gRPC data plane).
        builder.Services.AddRunnerHostServer(o => o.SigningKey = signingKey);
        builder.Services.AddAuthentication().AddJwtBearer("RunnerJwt", o =>
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = "mintokei-api",
                ValidateAudience = true, ValidAudience = "mintokei-runner",
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
                ClockSkew = TimeSpan.FromSeconds(30),
            });
        builder.Services.AddAuthorization(o =>
            o.AddPolicy("Runner", p => p.AddAuthenticationSchemes("RunnerJwt")
                                        .RequireAuthenticatedUser()
                                        .RequireClaim("machine_id")));

        // Control plane + one backend (drives the CLI inside the sandbox).
        builder.Services.AddAgentControlPlane();
        builder.Services.AddSingleton<IAgentBackend, ClaudeBackend>();

        // The sandbox layer (DockerSandboxRuntime + SandboxManager), bound from the "Sandbox" section
        // (Backend / Image / profiles) — the same config-form registration the Mintokei API uses.
        builder.Services.AddMintokeiSandbox(builder.Configuration);

        // gRPC endpoint (its own HTTP/2 port — configured under Kestrel in appsettings.json).
        builder.Services.AddGrpc();
        builder.Services.AddScoped<RunnerLinkService>();

        return builder;
    }

    public static WebApplication UseSandboxDemoBackend(this WebApplication app)
    {
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>().Database.EnsureCreated();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGroup("/api").MapRunnerHost();                                     // runner enroll / token exchange
        app.MapGrpcService<RunnerLinkService>().RequireAuthorization("Runner");   // gRPC data plane
        return app;
    }
}

/// <summary>No-op remote-process recovery (see RemoteRunnerMinimal): this host keeps nothing across a
/// restart, so a lost correlation simply ends its session.</summary>
public sealed class NoOpRemoteProcessRecovery : IRemoteProcessRecovery
{
    public Task<RemoteProcessHandle?> TryRecoverAsync(Guid correlationId, Guid machineId) =>
        Task.FromResult<RemoteProcessHandle?>(null);
}
