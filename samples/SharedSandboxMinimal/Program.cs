using Mintokei.Runner.Host.Hosting;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Hosting;

// =============================================================================
// TWO SESSIONS, ONE SANDBOX.
//
// A sandbox normally serves one session. It can serve several — every task in a workspace on one working
// tree — but sharing a sandbox shares more than the tree, and that is what this sample is about.
//
// A sandbox has ONE broker on ONE network, and the broker cannot tell which session a connection came from.
// Its egress allowlist and the credentials it injects apply to EVERY session inside. So letting a session
// join a sandbox that was not built for its tool silently hands the sessions already in there that tool's
// network reach and credentials — permanently, because the allowlist is fixed when the broker starts.
//
// Hence two halves, and the second is the one that is easy to skip:
//   1. DECLARE what a sandbox serves      (AdmittedTools, at provision time)
//   2. CHECK before anyone joins          (EnsureCanAttachAsync, at attach time)
//
// Prerequisites are the same as SandboxLifecycleExplicit — this launches real containers.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);
builder.AddMintokeiRunnerHost().AddClaude();
builder.AddSandboxAgentHost().AddClaude();

var app = builder.Build();

app.MapPost("/demo/shared", async (
    SandboxProvisioner provisioner,
    string prompt,
    string? repo,
    CancellationToken ct) =>
{
    // The key is what OWNS the working tree. Keying by session is what makes a sandbox unshareable — every
    // session would get its own tree and there would be nothing to join.
    var workspaceKey = Guid.NewGuid();

    var sandbox = await provisioner.ProvisionAsync(new SandboxProvisionRequest
    {
        Repos = repo is null ? [] : [new SandboxRepoSpec(repo)],

        // Omit this and the sandbox is single-session FOREVER: with nothing declared there is nothing for a
        // joiner to be checked against, and the gate below refuses rather than guessing.
        AdmittedTools = ["ClaudeCodeCli"],

        PersistentWorkspaceKey = workspaceKey,

        // One container, one cgroup. N sessions draw on a single memory limit and the OOM-killer takes the
        // container — every session in it, mid-turn. Size the CEILING for what the sandbox may host; leave
        // the reserve null, since that is what decides how many sandboxes fit a node.
        LimitsOverride = new SandboxResources(
            MemoryLimitBytes: 8L * 1024 * 1024 * 1024, CpuLimit: 2, PidsLimit: 512),
    }, ct);

    // ── session one: dispatch into the sandbox you just made. Nothing to check — nobody else is inside.
    var first = await RunSessionAsync(sandbox.MachineId, prompt, ct);

    // ── session two: JOIN. The gate reads the declaration off the sandbox itself, not from any local
    //    record — a stale record is exactly how a session gets into a sandbox that cannot serve it.
    try
    {
        await provisioner.EnsureCanAttachAsync(sandbox.Handle, "ClaudeCodeCli", ct);
    }
    catch (SandboxAdmissionException ex)
    {
        // Provision a separate sandbox instead. Refusing is the safe outcome, not an error to work around.
        return Results.Conflict(ex.Message);
    }

    var second = await RunSessionAsync(sandbox.MachineId, prompt, ct);

    // ── and the refusal, which is the half worth seeing: a DIFFERENT tool is turned away, because admitting
    //    it would widen the broker for the two sessions already running above.
    string refusal;
    try
    {
        await provisioner.EnsureCanAttachAsync(sandbox.Handle, "CodexCli", ct);
        refusal = "UNEXPECTED: a codex session was admitted to a claude-only sandbox";
    }
    catch (SandboxAdmissionException ex)
    {
        refusal = ex.Message;
    }

    return Results.Ok(new { workspaceKey, sandbox = sandbox.Name, first, second, refusal });

    // Dispatch a session to the sandbox's runner through your control plane — see SandboxRunnerHostMinimal
    // for the full version; the point here is only that BOTH sessions target the SAME machine id.
    static Task<string> RunSessionAsync(Guid machineId, string prompt, CancellationToken ct)
        => Task.FromResult($"session dispatched to machine {machineId}: {prompt}");
});

app.Run();
