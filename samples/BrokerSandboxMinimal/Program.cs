using System.Text;
using Microsoft.Extensions.Options;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;
using Mintokei.Runner.Host.Hosting;
using Mintokei.Runner.Host.Server;
using Mintokei.Sandbox;
using BrokerSandboxMinimal;

// =============================================================================
// A BROKER-EGRESS sandbox on a remote worker — the hardened posture.
//
// Same shape as RemoteSandboxMinimal, but the profile selects SandboxEgress.Broker: instead of staging
// credentials INTO the container, RemoteSandboxManager launches a per-session broker (a deny-by-default
// --internal network + a broker container). The sandbox can reach ONLY allowlisted hosts, gets git creds it
// never stores (fetched from the broker on demand), and calls the model API with a key it never holds
// (re-originated by the broker). None of the three secrets ever enter the box.
//
//   1. connect a worker with Docker + the sandbox image + the broker image (mintokei/sandbox-broker)
//   2. GET  /demo/workers                    → connected worker machine ids
//   3. POST /demo/broker-sandbox-run?host=<id>&prompt=...&repo=<optional private git url>
//
// Broker-mode constraints (see README): the backend/gRPC URLs must be https AND their host must be in the
// profile's EgressAllowlist (the runner dials the backend through the broker's TLS-only CONNECT proxy).
// =============================================================================

var builder = WebApplication.CreateBuilder(args);
builder.AddMintokeiRunnerHost().AddClaude();
builder.Services.AddMintokeiSandbox(builder.Configuration);   // spec factory + isolation profiles (incl. the broker profile)
builder.Services.AddMintokeiRemoteSandbox();                  // RemoteSandboxManager + RemoteDockerSandboxRuntime + ISandboxBroker
builder.Services.Configure<BrokerDemoOptions>(builder.Configuration.GetSection(BrokerDemoOptions.Section));

var app = builder.Build();
app.MapMintokeiRunnerHost();

app.MapGet("/demo/workers", (IRunnerRegistry registry) =>
    Results.Json(registry.ListRunners().Select(r => r.MachineId)));

app.MapPost("/demo/broker-sandbox-run", async (
    IRunnerEnrollment enroll,
    RemoteSandboxManager sandboxes,
    IAgentControlPlane plane,
    IRunnerRegistry registry,
    IOptions<BrokerDemoOptions> options,
    Guid host, string prompt, string? repo, CancellationToken ct) =>
{
    var o = options.Value;
    if (!registry.IsRunnerConnected(host))
        return Results.BadRequest($"worker {host} is not connected. GET /demo/workers to list connected runners.");

    var name = $"sandbox-hardened-{Guid.NewGuid().ToString("N")[..12]}";

    // ── Caller policy #1: mint the sandbox's identity (your enrollment) on the broker profile. ──
    var enrolled = await enroll.CreateEnrollmentTokenAsync(
        createdByUserName: "broker-sandbox-demo", machineName: name, isEphemeral: true, profile: "hardened");
    if (enrolled.MachineId is not { } machineId)
        return Results.Problem("enrollment did not pre-create a machine id");

    // ── Caller policy #2: the request carries NO credentials — broker mode injects them. AddHostGateway
    //    MUST be false (host reachability would defeat containment; the runtime rejects it otherwise). ──
    var request = new SandboxSessionRequest
    {
        BackendUrl = o.BackendUrl,
        GrpcBackendUrl = o.GrpcBackendUrl,
        EnrollmentToken = enrolled.Token,
        Name = name,
        AddHostGateway = false,
        Repos = string.IsNullOrWhiteSpace(repo) ? [] : [new SandboxRepoSpec(repo)],
    };

    // ── The secrets the broker holds on the worker (never seeded into the sandbox). ──
    var secrets = new SandboxBrokerSecrets(
        GitCredentials: o.GitCredentials,
        ModelUpstream: o.ModelUpstream,
        ModelAuth: o.ModelAuth);

    // ── ONE call: start the broker + net → docker run on the worker (joined to the internal net) → wait
    //    online. `await using` recycles the container AND the broker. ──
    RemoteSandboxSession sandbox;
    try
    {
        sandbox = await sandboxes.LaunchAsync(host, machineId, request, plane.IsRunnerConnected,
            profile: "hardened", brokerSecrets: secrets, ct: ct);
    }
    catch (SandboxRuntimeException ex)
    {
        return Results.BadRequest($"could not launch the broker sandbox on worker {host}: {ex.Message}");
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
            await foreach (var evt in session.Output)
            {
                if (evt is MessageOutput m)
                    transcript.AppendLine($"[{m.Message.Role}/{m.Message.Type}] {m.Message.Content}");
                if (evt is TurnEnded)
                    break;
            }
            return Results.Text(transcript.ToString());
        }
        finally
        {
            await plane.StopSessionAsync(session.SessionId);
        }
    } // ← disposed here: docker rm the sandbox + stop the broker + remove its network, all on the worker
});

app.Logger.LogInformation("──────────────────────────────────────────────────────────────");
app.Logger.LogInformation("BrokerSandboxMinimal is up (hardened / broker egress). Connect a worker, then:");
app.Logger.LogInformation("  GET  http://localhost:5086/demo/workers");
app.Logger.LogInformation("  curl -X POST 'http://localhost:5086/demo/broker-sandbox-run?host=<worker-id>&repo=<git-url>&prompt=hi'");
app.Logger.LogInformation("Needs: a worker with Docker + the sandbox image + mintokei/sandbox-broker; https+allowlisted backend URLs.");
app.Logger.LogInformation("──────────────────────────────────────────────────────────────");

app.Run();
