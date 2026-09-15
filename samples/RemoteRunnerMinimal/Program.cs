using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.AgentEngine.Contracts;
using Mintokei.Runner.Contracts.Messages;
using Mintokei.Runner.Host;
using Mintokei.Runner.Host.Persistence;
using Mintokei.Runner.Host.RemoteExecution;
using Mintokei.Runner.Host.RemoteExecution.Grpc;
using Mintokei.Runner.Host.Server;
using RemoteRunnerMinimal;

// =============================================================================
// Minimal remote-runner host.
//
// Accepts a Mintokei runner over gRPC and spawns agent CLIs on it — the smallest
// host that does so. What makes it "minimal":
//   * NO IRunnerHost implementation.        The transport falls back to the library's
//                                           NullRunnerHost (registered by AddRunnerHostCore).
//   * NO product database.                  RunnerHostDbContext runs on throwaway in-memory
//                                           SQLite (EnsureCreated once, gone on exit).
//   * ONE tiny seam we still supply.        IRemoteProcessRecovery (a no-op — recovery only
//                                           matters across a host restart, which we never do).
// Everything else — presence, the durable outbox, output streaming — is the library.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// A random HMAC key for the runner JWTs (shared between minting and validation). A real host would
// load this from configuration/secret storage so tokens survive a restart.
var signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

// --- (1) Throwaway in-memory SQLite for the 5 runner-infra tables. -----------------------------
// A named shared-cache in-memory DB kept alive by one open connection for the process lifetime;
// every RunnerHostDbContext opens its own connection to the same in-memory DB. No file, no product
// tables, no migrations — schema is built once by EnsureCreated() below.
const string dbConnectionString = "Data Source=RemoteRunnerMinimal;Mode=Memory;Cache=Shared";
var keepAliveConnection = new SqliteConnection(dbConnectionString);
keepAliveConnection.Open();
builder.Services.AddDbContext<RunnerHostDbContext>(o => o.UseSqlite(dbConnectionString));

// --- (2) The transport core. -------------------------------------------------------------------
// Optionally hand it a CLI-probe list via options so a connecting runner discovers + reports Claude
// (leave CliProbesProvider unset for zero discovery — task execution works either way). The reaction
// seam (IRunnerHost) is NOT registered here: AddRunnerHostCore TryAdds NullRunnerHost.
builder.Services.AddRunnerHostCore(o =>
    o.CliProbesProvider = _ => Task.FromResult<IReadOnlyList<CliProbeSpec>>(
    [
        new CliProbeSpec("ClaudeCodeCli", "claude", "--version", null),
    ]));

// Runner.Host's ICommandLineRunnerFactory resolves the engine's local runner for null-machine (local)
// spawns; register it so the factory can be constructed. (Remote spawns go over the outbox instead.)
builder.Services.AddSingleton<ICommandLineRunner, CommandLineRunner>();

// The one seam a minimal host still supplies (a no-op — see NoOpRemoteProcessRecovery).
builder.Services.AddSingleton<IRemoteProcessRecovery, NoOpRemoteProcessRecovery>();

// --- (3) Runner enrollment + JWT auth. ---------------------------------------------------------
// AddRunnerHostServer mints runner tokens (carrying the machine_id claim) and registers the
// enrollment handlers behind MapRunnerHost(). The JWT bearer scheme validates those same tokens.
builder.Services.AddRunnerHostServer(o => o.SigningKey = signingKey);   // Issuer/Audience keep their defaults
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

// --- (4) The control plane + at least one backend. ---------------------------------------------
// AddAgentControlPlane composes the backends below and the ICommandLineRunnerFactory that
// AddRunnerHostCore registered (the remote-capable one), so StartSessionAsync(spec, machineId)
// spawns the CLI on that runner.
builder.Services.AddAgentControlPlane();
builder.Services.AddSingleton<IAgentBackend, ClaudeBackend>();

// --- (5) gRPC endpoint (its own HTTP/2 port — configured in appsettings.json). -----------------
builder.Services.AddGrpc();
builder.Services.AddScoped<RunnerLinkService>();

var app = builder.Build();

// Build the 5 runner-infra tables once in the in-memory DB.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>().Database.EnsureCreated();

app.UseAuthentication();
app.UseAuthorization();

// Runner-facing HTTP surface (enroll / token exchange) + the gRPC data plane.
// Group the HTTP endpoints under /api so the sample matches the default runner-client URLs.
app.MapGroup("/api").MapRunnerHost();
app.MapGrpcService<RunnerLinkService>().RequireAuthorization("Runner");

// --- Demo endpoints -----------------------------------------------------------------------------

// The runners connected right now.
app.MapGet("/demo/runners", (IAgentControlPlane plane) =>
    plane.ListRunners().Select(r => r.MachineId));

// Mint another one-time enrollment token on demand.
app.MapPost("/demo/enroll-token", async (IRunnerEnrollment enroll) =>
    Results.Ok((await enroll.CreateEnrollmentTokenAsync()).Token));

// Run one prompt on the first connected runner and return its transcript for that turn.
app.MapPost("/demo/run", async (IAgentControlPlane plane, string prompt, string? dir) =>
{
    var runner = plane.ListRunners().FirstOrDefault();
    if (runner is null)
        return Results.BadRequest("No runner connected — enroll one first (see the console for a token).");

    var spec = new AgentSessionSpec
    {
        Tool = AgentToolKey.ClaudeCodeCli,
        WorkingDirectory = dir ?? "/tmp",
    };

    var session = await plane.StartSessionAsync(spec, runnerMachineId: runner.MachineId);
    try
    {
        await session.SendMessageAsync(prompt);

        var transcript = new StringBuilder();
        await foreach (var evt in session.Output)     // streams back over gRPC from the runner
        {
            if (evt is MessageOutput m)
                transcript.AppendLine($"[{m.Message.Role}/{m.Message.Type}] {m.Message.Content}");
            if (evt is TurnEnded)
                break;                                  // stop after the first completed turn
        }
        return Results.Text(transcript.ToString());
    }
    finally
    {
        await plane.StopSessionAsync(session.SessionId);
    }
});

// Log when a runner attaches (the presence event the control plane raises — no polling needed).
var controlPlane = app.Services.GetRequiredService<IAgentControlPlane>();
controlPlane.RunnerConnected += info => SampleLog.RunnerConnected(app.Logger, info.MachineId);

// Mint + print one enrollment token so you can attach a runner immediately.
using (var scope = app.Services.CreateScope())
{
    var token = (await scope.ServiceProvider.GetRequiredService<IRunnerEnrollment>().CreateEnrollmentTokenAsync()).Token;
    app.Logger.LogInformation("──────────────────────────────────────────────────────────────");
    app.Logger.LogInformation("Enrollment token (valid ~15 min):");
    SampleLog.EnrollmentToken(app.Logger, token);
    app.Logger.LogInformation("Attach a runner:");
    app.Logger.LogInformation("  Runner__GrpcBackendUrl=http://localhost:5081 \\");
    app.Logger.LogInformation("  dotnet run --project src/Mintokei.Runner -- \\");
    SampleLog.AttachCommand(app.Logger, token);
    app.Logger.LogInformation("──────────────────────────────────────────────────────────────");
}

app.Run();

/// <summary>
/// Source-generated log methods for this sample's startup banner. Avoids the params-object[] array
/// (and Guid boxing) the plain logger.LogInformation(...) calls allocate on every invocation, which
/// the CA1873 analyzer flags; templates are unchanged.
/// </summary>
internal static partial class SampleLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Runner connected: {MachineId}")]
    public static partial void RunnerConnected(ILogger logger, Guid machineId);

    [LoggerMessage(Level = LogLevel.Information, Message = "  {Token}")]
    public static partial void EnrollmentToken(ILogger logger, string token);

    [LoggerMessage(Level = LogLevel.Information, Message = "  --backend http://localhost:5080 --token {Token} --data-dir ./runner-data")]
    public static partial void AttachCommand(ILogger logger, string token);
}
