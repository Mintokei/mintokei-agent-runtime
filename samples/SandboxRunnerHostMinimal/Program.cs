using Mintokei.AgentEngine.AgentTools;
using Mintokei.Sandbox.Hosting;

// =============================================================================
// A GENUINELY-REAL sandbox host — no Fake* types.
//
// Two calls make the backend, one call runs the agent:
//
//   AddSandboxAgentHost()   db + transport + JWT + control plane + gRPC + the sandbox layer
//   MapSandboxAgentHost()   auth + enroll routes + the gRPC data plane
//   host.RunAsync(...)      mint a one-time enrollment token (pre-creating the machine identity) →
//                           launch the sandbox → wait for the in-container runner to connect →
//                           dispatch the agent session into it → stream it → recycle on dispose
//
// The backend is a config choice, not a code choice: Sandbox:Backend = docker runs a container here,
// kubernetes runs a pod in the cluster, and setting SandboxAgentRequest.HostMachineId runs it on a
// connected remote worker — all with the code below unchanged.
//
// NOT "runs anywhere": it launches a real container — see the README for the prerequisites.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);
builder.AddSandboxAgentHost().AddClaude();   // + .AddCodex() / .AddRemoteWorkers()

var app = builder.Build();
app.MapSandboxAgentHost();

app.MapPost("/demo/sandbox-run", async (
    SandboxAgentHost host, string prompt, string? repo, CancellationToken ct) =>
{
    try
    {
        // Provisions the sandbox, waits for it to come online, starts the session, sends the prompt.
        await using var run = await host.RunAsync(new SandboxAgentRequest
        {
            Tool = AgentToolKey.ClaudeCodeCli,
            Repo = repo,      // cloned to /repos/<name>; the session starts there
            Prompt = prompt,
        }, ct);

        // One-shot: collect the transcript until the turn ends. Use run.Output for deltas / tool calls /
        // permission prompts, or run.SendMessageAsync(...) to keep the conversation going.
        var turn = await run.CollectTurnAsync(ct);
        return Results.Text(turn.Transcript);
    }                                        // ← disposed here: session stopped, container removed
    catch (SandboxAgentException ex)
    {
        // Never-came-online failures carry the container's tail logs — usually a failed clone, missing
        // credentials, an unpullable image, or a backend URL the container can't reach.
        return Results.Problem(string.IsNullOrWhiteSpace(ex.ContainerLogs)
            ? ex.Message
            : $"{ex.Message}\n\n--- sandbox logs ---\n{ex.ContainerLogs}");
    }
});

app.Logger.LogInformation("──────────────────────────────────────────────────────────────");
app.Logger.LogInformation("SandboxRunnerHostMinimal is up. Provision a sandbox + run one turn:");
app.Logger.LogInformation("  curl -X POST 'http://localhost:5082/demo/sandbox-run?repo=<git-url>&prompt=hi'");
app.Logger.LogInformation("Needs: Docker + the sandbox image (Sandbox:Image) + host reachable from the container.");
app.Logger.LogInformation("──────────────────────────────────────────────────────────────");

app.Run();
