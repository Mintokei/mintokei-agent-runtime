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
// Same lifecycle as SandboxRunnerHostMinimal, but instead of `docker run` on THIS host it dispatches the
// container to a CHOSEN, already-connected worker over the control channel — using the pieces this repo
// exposes for exactly that: RemoteDockerSandboxRuntime + SandboxCredentialStager (AddMintokeiRemoteSandbox()).
//
//   1. connect a worker:  dotnet run --project src/Mintokei.Runner -- --backend ... --token ... (see README)
//   2. GET  /demo/workers                       → list connected worker machine ids
//   3. POST /demo/remote-sandbox-run?host=<id>&prompt=...&repo=<optional git url>
//        a. mint a one-time token (pre-creates the sandbox's ephemeral machine id)
//        b. STAGE the worker's creds into a uid-readable copy on the worker (non-root container can read them)
//        c. dispatch `docker run` of the sandbox image TO the worker
//        d. wait for the in-container runner to enroll + connect back over gRPC
//        e. dispatch an agent session INTO the container (same IAgentSession API)
//        f. recycle: `docker rm` + remove the staged creds, both on the worker
//
// NOT "runs anywhere": needs a connected worker with Docker + the sandbox image — see the README.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);
builder.AddMintokeiRunnerHost().AddClaude();                 // real Runner.Host + control plane + gRPC (config-driven)
builder.Services.AddMintokeiSandbox(builder.Configuration);  // spec factory + isolation profiles + Sandbox options
builder.Services.AddMintokeiRemoteSandbox();                 // RemoteDockerSandboxRuntime + SandboxCredentialStager
builder.Services.Configure<RemoteDemoOptions>(builder.Configuration.GetSection(RemoteDemoOptions.Section));

var app = builder.Build();
app.MapMintokeiRunnerHost();                                 // db init + auth + enroll routes + gRPC data plane

// List the workers you can target (their machine ids come from the "Runner connected" log too).
app.MapGet("/demo/workers", (IRunnerRegistry registry) =>
    Results.Json(registry.ListRunners().Select(r => r.MachineId)));

app.MapPost("/demo/remote-sandbox-run", async (
    IRunnerEnrollment enroll,
    RemoteDockerSandboxRuntime remote,
    SandboxCredentialStager stager,
    SandboxSpecFactory specFactory,
    SandboxProfileResolver profiles,
    IAgentControlPlane plane,
    IRunnerRegistry registry,
    IOptions<RemoteDemoOptions> options,
    ILoggerFactory loggerFactory,
    Guid host, string prompt, string? repo, CancellationToken ct) =>
{
    var o = options.Value;
    var log = loggerFactory.CreateLogger("remote-sandbox-run");

    // 0. The target worker must be a connected runner with a working Docker.
    if (!registry.IsRunnerConnected(host))
        return Results.Problem($"worker {host} is not connected. GET /demo/workers to list connected runners.");
    if (!await remote.ProbeDockerAsync(host, ct))
        return Results.Problem($"worker {host} has no working Docker on PATH.");

    var name = $"sandbox-standard-{Guid.NewGuid().ToString("N")[..12]}";

    // 1. Mint a one-time token that PRE-CREATES the sandbox's (ephemeral) machine identity, so we bind by id.
    var enrolled = await enroll.CreateEnrollmentTokenAsync(
        createdByUserName: "remote-sandbox-demo", machineName: name, isEphemeral: true, profile: "standard");
    if (enrolled.MachineId is not { } sandboxMachineId)
        return Results.Problem("enrollment did not pre-create a machine id");

    // 2. Credential SOURCES on the worker — its own ~/.claude etc. unless overridden in config.
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

    // 3. STAGE the creds into a uid-readable per-session copy ON THE WORKER, and mount THAT — the non-root
    //    container can't read the worker's own root-owned creds directly. This is SandboxCredentialStager.
    var staged = await stager.StageAsync(host, name, new SandboxSeedSources(
        request.ClaudeConfigHostDir, request.ClaudeConfigJsonHostFile,
        request.CodexConfigHostDir, request.GitCredentialsHostDir), ct);
    request = request with
    {
        ClaudeConfigHostDir = staged.ClaudeConfigDir,
        ClaudeConfigJsonHostFile = staged.ClaudeConfigJsonFile,
        CodexConfigHostDir = staged.CodexConfigDir,
        GitCredentialsHostDir = staged.GitCredentialsDir,
    };

    // 4. Build the docker-run spec and dispatch it TO THE WORKER (docker run happens there, not here).
    var spec = specFactory.Build(profiles.Resolve(), request);
    SandboxHandle handle;
    try
    {
        handle = await remote.ProvisionAsync(host, spec, ct);
    }
    catch (SandboxRuntimeException ex)
    {
        await stager.RemoveAsync(host, name, ct);
        log.LogError(ex, "remote sandbox provisioning failed");
        return Results.Problem($"could not launch the sandbox on worker {host}: {ex.Message}");
    }

    try
    {
        // 5. Wait (bounded) for the in-container runner to enroll + connect back over gRPC. Bail early if the
        //    container exits first (usually a repo-clone / git-creds error) and surface its logs.
        if (!await WaitOnlineAsync(plane, remote, host, handle, sandboxMachineId, ct))
        {
            var logs = await remote.GetLogsAsync(host, handle, 40, ct);
            return Results.Problem($"sandbox '{name}' never came online.\n{logs}");
        }

        log.LogInformation("sandbox '{Name}' (machine {MachineId}) online on worker {Host}; dispatching the session",
            name, sandboxMachineId, host);

        // 6. Dispatch the session INTO the container — the SAME IAgentSession API as any runner. The session
        //    runs in /repos/<name> (present only once a repo is cloned in), else the repos root.
        var workDir = string.IsNullOrWhiteSpace(repo) ? SandboxSpecFactory.RepoRoot : SandboxSpecFactory.DefaultSourcePath(repo);
        var session = await plane.StartSessionAsync(
            new AgentSessionSpec { Tool = AgentToolKey.ClaudeCodeCli, WorkingDirectory = workDir },
            runnerMachineId: sandboxMachineId, ct: ct);
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
    }
    finally
    {
        // 7. One-shot recycle: stop the container + remove the staged credential copy, both on the worker.
        await remote.StopAsync(host, handle, ct);
        await stager.RemoveAsync(host, name, ct);
    }
});

app.Logger.LogInformation("──────────────────────────────────────────────────────────────");
app.Logger.LogInformation("RemoteSandboxMinimal is up. Connect a worker, then:");
app.Logger.LogInformation("  GET  http://localhost:5084/demo/workers");
app.Logger.LogInformation("  curl -X POST 'http://localhost:5084/demo/remote-sandbox-run?host=<worker-id>&repo=<git-url>&prompt=hi'");
app.Logger.LogInformation("Needs: a connected worker with Docker + the sandbox image; URLs reachable from the worker.");
app.Logger.LogInformation("──────────────────────────────────────────────────────────────");

app.Run();

// Wait (bounded) for the pre-created sandbox machine to connect back; false if it never does (timeout, or the
// container exited first — the caller then surfaces the container logs).
static async Task<bool> WaitOnlineAsync(
    IAgentControlPlane plane, RemoteDockerSandboxRuntime remote, Guid host, SandboxHandle handle, Guid sandboxMachineId, CancellationToken ct)
{
    for (var i = 0; i < 120; i++)
    {
        if (plane.IsRunnerConnected(sandboxMachineId))
            return true;
        var status = await remote.GetStatusAsync(host, handle, ct);
        if (status.State is SandboxState.Exited or SandboxState.NotFound)
            return false;
        await Task.Delay(500, ct);
    }
    return false;
}
