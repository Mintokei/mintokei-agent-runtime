using System.Text;
using Microsoft.Extensions.Options;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;
using Mintokei.Runner.Contracts.Messages;
using Mintokei.Runner.Host.Server;
using Mintokei.Sandbox;
using SandboxRunnerHostMinimal;

// =============================================================================
// A GENUINELY-REAL sandbox host — no Fake* types.
//
// The backend wiring (Mintokei.Runner.Host + AgentControlPlane + Mintokei.Sandbox) lives in
// SandboxDemoBackend.cs; here we just add it and expose ONE endpoint that runs the whole on-demand
// lifecycle for real:
//
//   POST /demo/sandbox-run?prompt=...&repo=<optional git url>
//     1. mint a one-time enrollment token (pre-creating an ephemeral machine id)
//     2. `docker run` the sandbox image (the container's runner dials back in)
//     3. wait for that runner to enroll + connect over gRPC
//     4. dispatch an agent session INTO the container (same IAgentSession API)
//     5. recycle the container (`docker rm`)
//
// URLs + optional credentials come from the "Sandbox" config section (appsettings / env / args), bound
// to SandboxDemoOptions. NOT "runs anywhere": step 2 launches a real container — see the README for the
// prerequisites (Docker, the image, host reachable from the container, creds for a real turn).
// =============================================================================

var builder = WebApplication.CreateBuilder(args);
builder.AddSandboxDemoBackend();     // real Runner.Host + AgentControlPlane + Mintokei.Sandbox (config-driven)

var app = builder.Build();
app.UseSandboxDemoBackend();         // db init + auth + MapRunnerHost + gRPC data plane

// The full sandbox lifecycle, over one endpoint (no fakes).
app.MapPost("/demo/sandbox-run", async (
    IRunnerEnrollment enroll, SandboxManager manager, IAgentControlPlane plane,
    IOptions<SandboxDemoOptions> options, ILoggerFactory loggerFactory,
    string prompt, string? repo, CancellationToken ct) =>
{
    var o = options.Value;
    var log = loggerFactory.CreateLogger("sandbox-run");
    var name = $"sandbox-standard-{Guid.NewGuid().ToString("N")[..12]}";

    // 1. Mint a one-time token that PRE-CREATES the ephemeral machine identity, so we bind by id (not by
    //    discovering the runner by name). This is what SandboxSessionRequestFactory does in the product.
    var enrolled = await enroll.CreateEnrollmentTokenAsync(
        createdByUserName: "sandbox-demo", machineName: name, isEphemeral: true, profile: "standard");
    if (enrolled.MachineId is not { } machineId)
        return Results.Problem("enrollment did not pre-create a machine id");

    // 2. Describe the session — the real SandboxSessionRequest the product builds. URLs + creds from config.
    var request = new SandboxSessionRequest
    {
        BackendUrl = o.BackendUrl,
        GrpcBackendUrl = o.GrpcBackendUrl,
        EnrollmentToken = enrolled.Token,
        Name = name,
        AddHostGateway = true, // dev-only: --add-host=host.docker.internal:host-gateway
        Repos = string.IsNullOrWhiteSpace(repo) ? [] : [new SandboxRepoSpec(repo)],
        ClaudeConfigHostDir = o.ClaudeConfigHostDir,
        ClaudeConfigJsonHostFile = o.ClaudeConfigJsonHostFile,
        CodexConfigHostDir = o.CodexConfigHostDir,
        GitCredentialsHostDir = o.GitCredentialsHostDir,
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
        // 4. Wait (bounded) for the in-container runner to enroll + connect over gRPC. Bailing early on a
        //    container that exits first surfaces its logs — almost always a repo-clone / git-creds error.
        if (!await WaitOnlineAsync(plane, manager, lease, machineId, ct))
        {
            var logs = await manager.GetLogsAsync(lease.Handle, 40, ct);
            return Results.Problem($"sandbox '{name}' never came online.\n{logs}");
        }

        log.LogInformation("sandbox '{Name}' (machine {MachineId}) is online; dispatching the session", name, machineId);

        // 5. Dispatch the session INTO the sandbox — the SAME IAgentSession API as any remote runner. The
        //    session runs in /repos/<name> (present only after a repo is cloned), else the repo root.
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
app.Logger.LogInformation("  curl -X POST 'http://localhost:5082/demo/sandbox-run?repo=<git-url>&prompt=hi'");
app.Logger.LogInformation("Needs: Docker + the sandbox image (Sandbox:Image) + host reachable from the container.");
app.Logger.LogInformation("──────────────────────────────────────────────────────────────");

app.Run();

// Wait (bounded) for the pre-created machine to come online; false if it never does (timeout or the
// container exited first — the caller then surfaces the container logs).
static async Task<bool> WaitOnlineAsync(
    IAgentControlPlane plane, SandboxManager manager, SandboxLease lease, Guid machineId, CancellationToken ct)
{
    for (var i = 0; i < 120; i++)
    {
        if (plane.IsRunnerConnected(machineId))
            return true;
        var status = await manager.GetStatusAsync(lease.Handle, ct);
        if (status.State is SandboxState.Exited or SandboxState.NotFound)
            return false;
        await Task.Delay(500, ct);
    }
    return false;
}
