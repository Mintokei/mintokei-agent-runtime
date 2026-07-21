using System.Text;
using Microsoft.Extensions.Options;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;
using Mintokei.Runner.Host.Hosting;
using Mintokei.Runner.Host.Server;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Docker;
using RemoteSandboxMinimal;

// =============================================================================
// A sandbox on a REMOTE worker — no Fake* types.
//
// The distributed twin of SandboxRunnerHostMinimal: instead of `docker run` on THIS host, the container is
// dispatched to a CHOSEN, already-connected worker over the control channel. All the mechanical steps
// (probe Docker → stage creds uid-readable on the worker → docker run there → wait for the in-container
// runner to connect back → recycle) are one call — RemoteSandboxManager.LaunchAsync (AddMintokeiRemoteSandbox).
//
//   1. connect a worker:  dotnet run --project src/Mintokei.Runner -- --backend ... --token ... (see README)
//   2. GET  /demo/workers                       → list connected worker machine ids
//   3. POST /demo/remote-sandbox-run?host=<id>&prompt=...&repo=<optional git url>
//
// NOT "runs anywhere": needs a connected worker with Docker + the sandbox image — see the README.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);
builder.AddMintokeiRunnerHost().AddClaude();                 // real Runner.Host + control plane + gRPC (config-driven)
builder.Services.AddMintokeiSandbox(builder.Configuration);  // spec factory + isolation profiles + Sandbox options
builder.Services.AddMintokeiRemoteSandbox();                 // RemoteSandboxManager + RemoteDockerSandboxRuntime + stager
builder.Services.Configure<RemoteDemoOptions>(builder.Configuration.GetSection(RemoteDemoOptions.Section));

var app = builder.Build();
app.MapMintokeiRunnerHost();                                 // db init + auth + enroll routes + gRPC data plane

// List the workers you can target (their machine ids come from the "Runner connected" log too).
app.MapGet("/demo/workers", (IRunnerRegistry registry) =>
    Results.Json(registry.ListRunners().Select(r => r.MachineId)));

app.MapPost("/demo/remote-sandbox-run", async (
    IRunnerEnrollment enroll,
    RemoteSandboxManager sandboxes,
    RemoteDockerSandboxRuntime remote,
    IAgentControlPlane plane,
    IRunnerRegistry registry,
    IOptions<RemoteDemoOptions> options,
    Guid host, string prompt, string? repo, CancellationToken ct) =>
{
    var o = options.Value;
    if (!registry.IsRunnerConnected(host))
        return Results.Problem($"worker {host} is not connected. GET /demo/workers to list connected runners.");

    var name = $"sandbox-standard-{Guid.NewGuid().ToString("N")[..12]}";

    // ── Caller policy #1: mint the sandbox's identity (your enrollment). ──
    var enrolled = await enroll.CreateEnrollmentTokenAsync(
        createdByUserName: "remote-sandbox-demo", machineName: name, isEphemeral: true, profile: "standard");
    if (enrolled.MachineId is not { } machineId)
        return Results.Problem("enrollment did not pre-create a machine id");

    // ── Caller policy #2: build the request — creds/urls/repo. Creds default to the WORKER's own ~/.claude. ──
    var home = await remote.ProbeHomeAsync(host, ct);
    var request = new SandboxSessionRequest
    {
        BackendUrl = o.BackendUrl,
        GrpcBackendUrl = o.GrpcBackendUrl,
        EnrollmentToken = enrolled.Token,
        Name = name,
        AddHostGateway = o.AddHostGateway,
        Repos = string.IsNullOrWhiteSpace(repo) ? [] : [new SandboxRepoSpec(repo)],
        ClaudeConfigHostDir = string.IsNullOrWhiteSpace(o.ClaudeConfigHostDir) ? $"{home}/.claude" : o.ClaudeConfigHostDir,
        ClaudeConfigJsonHostFile = string.IsNullOrWhiteSpace(o.ClaudeConfigJsonHostFile) ? $"{home}/.claude.json" : o.ClaudeConfigJsonHostFile,
        CodexConfigHostDir = string.IsNullOrWhiteSpace(o.CodexConfigHostDir) ? $"{home}/.codex" : o.CodexConfigHostDir,
        GitCredentialsHostDir = string.IsNullOrWhiteSpace(o.GitCredentialsHostDir) ? home : o.GitCredentialsHostDir,
    };

    // ── ONE call: probe → stage creds → docker run on the worker → wait online. `await using` recycles. ──
    RemoteSandboxSession sandbox;
    try
    {
        sandbox = await sandboxes.LaunchAsync(host, machineId, request, plane.IsRunnerConnected, ct: ct);
    }
    catch (SandboxRuntimeException ex)
    {
        return Results.Problem($"could not launch the sandbox on worker {host}: {ex.Message}");
    }

    await using (sandbox)
    {
        // ── Caller policy #3: dispatch the session into the sandbox (same IAgentSession API) + stream it back. ──
        var workDir = string.IsNullOrWhiteSpace(repo) ? SandboxSpecFactory.RepoRoot : SandboxSpecFactory.DefaultSourcePath(repo);
        var session = await plane.StartSessionAsync(
            new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli, WorkingDirectory = workDir },
            runnerMachineId: sandbox.MachineId, ct: ct);
        try
        {
            await session.SendMessageAsync(prompt);

            var transcript = new StringBuilder();
            await foreach (var evt in session.Output)          // streams back over gRPC from inside the container
            {
                if (evt is MessageOutput m)
                    transcript.AppendLine($"[{m.Message.Role}/{m.Message.Type}] {m.Message.Content}");
                if (evt is TurnEnded)
                    break;                                      // stop after the first completed turn
            }
            return Results.Text(transcript.ToString());
        }
        finally
        {
            await plane.StopSessionAsync(session.SessionId);
        }
    } // ← sandbox disposed here: docker rm + remove the staged creds, both on the worker
});

app.Logger.LogInformation("──────────────────────────────────────────────────────────────");
app.Logger.LogInformation("RemoteSandboxMinimal is up. Connect a worker, then:");
app.Logger.LogInformation("  GET  http://localhost:5084/demo/workers");
app.Logger.LogInformation("  curl -X POST 'http://localhost:5084/demo/remote-sandbox-run?host=<worker-id>&repo=<git-url>&prompt=hi'");
app.Logger.LogInformation("Needs: a connected worker with Docker + the sandbox image; URLs reachable from the worker.");
app.Logger.LogInformation("──────────────────────────────────────────────────────────────");

app.Run();
