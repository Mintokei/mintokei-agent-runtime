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
using Mintokei.Sandbox;
using SandboxRunnerHostMinimal;

// =============================================================================
// A GENUINELY-REAL sandbox host — no Fake* types.
//
// It hosts the real Mintokei.Runner.Host backend (the exact wiring from
// RemoteRunnerMinimal) AND Mintokei.Sandbox, then exposes ONE endpoint that runs
// the whole on-demand lifecycle for real:
//
//   POST /demo/sandbox-run?prompt=...&repo=<optional git url>
//     1. mint a one-time enrollment token (pre-creating an ephemeral machine id)
//     2. `docker run` the sandbox image (the container's runner dials back in)
//     3. wait for that runner to enroll + connect over gRPC
//     4. dispatch an agent session INTO the container (same IAgentSession API)
//     5. recycle the container (`docker rm`)
//
// Unlike the other samples this is NOT "runs anywhere": step 2 launches a real
// container. Prerequisites (see README): Docker running, the sandbox image
// present (Sandbox:Image), and this host reachable from the container at
// Sandbox:BackendUrl / Sandbox:GrpcBackendUrl. The only thing a product adds on
// top is persistence + policy (which task → which sandbox, repos/creds, a warm
// pool, a reaper); every runtime call below is reused as-is.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// A random HMAC key for the runner JWTs (shared between minting and validation).
var signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

// --- (1) Throwaway in-memory SQLite for the runner-infra tables. --------------------------------
const string dbConnectionString = "Data Source=SandboxRunnerHostMinimal;Mode=Memory;Cache=Shared";
var keepAliveConnection = new SqliteConnection(dbConnectionString);
keepAliveConnection.Open();
builder.Services.AddDbContext<RunnerHostDbContext>(o => o.UseSqlite(dbConnectionString));

// --- (2) The transport core + local command runner + the one no-op recovery seam. --------------
builder.Services.AddRunnerHostCore();
builder.Services.AddSingleton<ICommandLineRunner, CommandLineRunner>();
builder.Services.AddSingleton<IRemoteProcessRecovery, NoOpRemoteProcessRecovery>();

// --- (3) Runner enrollment + JWT auth (validates the machine_id claim on the gRPC data plane). --
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

// --- (4) The control plane + at least one backend (drives the CLI inside the sandbox). ----------
builder.Services.AddAgentControlPlane();
builder.Services.AddSingleton<IAgentBackend, ClaudeBackend>();

// --- (5) The sandbox layer: real DockerSandboxRuntime + SandboxManager, bound in code. ----------
builder.Services.AddMintokeiSandbox(o =>
{
    o.Backend = "docker";
    o.Image = builder.Configuration["Sandbox:Image"] ?? "ghcr.io/mintokei/mintokei-sandbox:latest";
    o.DefaultProfile = "standard";
    o.AllowedProfiles = ["standard"];
    o.Profiles["standard"] = new SandboxProfileConfig { Runtime = "runc", MemoryMb = 4096, Cpus = 2 };
});

// --- (6) gRPC endpoint (its own HTTP/2 port — configured in appsettings.json). ------------------
builder.Services.AddGrpc();
builder.Services.AddScoped<RunnerLinkService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>().Database.EnsureCreated();

app.UseAuthentication();
app.UseAuthorization();

app.MapGroup("/api").MapRunnerHost();
app.MapGrpcService<RunnerLinkService>().RequireAuthorization("Runner");

// --- The full sandbox lifecycle, over one endpoint (no fakes). ----------------------------------
app.MapPost("/demo/sandbox-run", async (
    IRunnerEnrollment enroll, SandboxManager manager, IAgentControlPlane plane,
    IConfiguration cfg, ILoggerFactory loggerFactory, string prompt, string? repo, CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("sandbox-run");

    // Reachable-from-container URLs. AddHostGateway maps host.docker.internal → the host, so the
    // container's runner dials this host's Runner.Host over REST (enroll) and gRPC (control stream).
    var backendUrl = cfg["Sandbox:BackendUrl"] ?? "http://host.docker.internal:5082";
    var grpcUrl = cfg["Sandbox:GrpcBackendUrl"] ?? "http://host.docker.internal:5083";
    var name = $"sandbox-standard-{Guid.NewGuid().ToString("N")[..12]}";

    // 1. Mint a one-time token that PRE-CREATES the ephemeral machine identity, so we bind by id (not
    //    by discovering the runner by name after it enrolls). This is what SandboxSessionRequestFactory
    //    does in the product.
    var enrolled = await enroll.CreateEnrollmentTokenAsync(
        createdByUserName: "sandbox-demo", machineName: name, isEphemeral: true, profile: "standard");
    if (enrolled.MachineId is not { } machineId)
        return Results.Problem("enrollment did not pre-create a machine id");

    // 2. Describe the session — the real SandboxSessionRequest the product builds.
    var request = new SandboxSessionRequest
    {
        BackendUrl = backendUrl,
        GrpcBackendUrl = grpcUrl,
        EnrollmentToken = enrolled.Token,
        Name = name,
        AddHostGateway = true, // dev-only: --add-host=host.docker.internal:host-gateway
        Repos = string.IsNullOrWhiteSpace(repo) ? [] : [new SandboxRepoSpec(repo)],
        // Optional credential seeding: each host path is mounted RO at /seed and copied into the
        // container's HOME by the entrypoint, so the CLI is authenticated. Set the matching Sandbox:*
        // config keys to make the agent turn actually run; leave them unset for a plumbing-only demo.
        ClaudeConfigHostDir = cfg["Sandbox:ClaudeConfigHostDir"],
        ClaudeConfigJsonHostFile = cfg["Sandbox:ClaudeConfigJsonHostFile"],
        CodexConfigHostDir = cfg["Sandbox:CodexConfigHostDir"],
        GitCredentialsHostDir = cfg["Sandbox:GitCredentialsHostDir"],
    };

    // 3. Provision the REAL container (docker run of the sandbox image).
    SandboxLease lease;
    try
    {
        lease = await manager.ProvisionAsync(request, ct: ct);
    }
    catch (SandboxRuntimeException ex)
    {
        log.LogError(ex, "sandbox provisioning failed");
        return Results.Problem(
            $"Could not launch the sandbox container: {ex.Message}\n" +
            "Prerequisites: Docker running, the sandbox image present (Sandbox:Image), and this host " +
            "reachable from the container at Sandbox:BackendUrl / Sandbox:GrpcBackendUrl.");
    }

    try
    {
        // 4. Wait (bounded) for the in-container runner to enroll + connect over gRPC. Bail early if the
        //    container exits first — almost always a repo-clone / git-credentials error in the entrypoint.
        var online = false;
        for (var i = 0; i < 120 && !online; i++)
        {
            if (plane.IsRunnerConnected(machineId)) { online = true; break; }
            var status = await manager.GetStatusAsync(lease.Handle, ct);
            if (status.State is SandboxState.Exited or SandboxState.NotFound)
            {
                var logs = await manager.GetLogsAsync(lease.Handle, 40, ct);
                return Results.Problem($"sandbox '{name}' exited before enrolling.\n{logs}");
            }
            await Task.Delay(500, ct);
        }
        if (!online)
            return Results.Problem($"sandbox '{name}' did not come online within the timeout.");

        log.LogInformation("sandbox '{Name}' (machine {MachineId}) is online; dispatching the session", name, machineId);

        // 5. Dispatch the session INTO the sandbox — identical IAgentSession API as any remote runner.
        var workDir = string.IsNullOrWhiteSpace(repo) ? SandboxSpecFactory.RepoRoot : SandboxSpecFactory.DefaultSourcePath(repo);
        var spec = new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli, WorkingDirectory = workDir };
        var session = await plane.StartSessionAsync(spec, runnerMachineId: machineId, ct: ct);
        try
        {
            await session.SendMessageAsync(prompt);

            var transcript = new StringBuilder();
            await foreach (var evt in session.Output)     // streams back over gRPC from inside the container
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
    }
    finally
    {
        // 6. One-shot recycle — stop + remove the container (docker rm -f).
        await manager.RecycleAsync(request.Name, ct);
    }
});

app.Logger.LogInformation("──────────────────────────────────────────────────────────────");
app.Logger.LogInformation("SandboxRunnerHostMinimal is up. Provision a sandbox + run one turn:");
app.Logger.LogInformation("  curl -X POST 'http://localhost:5082/demo/sandbox-run?prompt=say%20hello'");
app.Logger.LogInformation("Needs: Docker + the sandbox image (Sandbox:Image) + host reachable from the container.");
app.Logger.LogInformation("──────────────────────────────────────────────────────────────");

app.Run();
