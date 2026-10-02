using Mintokei.AgentEngine;
using Mintokei.AgentEngine.Claude;

namespace Mintokei.Sandbox.Hosting;

public interface IAgentJsonExecutor
{
    Task<string> ExecuteAsync(SandboxAgentRequest request, CancellationToken ct = default);
}

/// <summary>One fresh, tool-free JSON turn. Keeps compute lifetime and cleanup in SandboxAgentHost.
/// No automatic retries: applications own checkpoints and decide when repeating inference is appropriate.</summary>
public sealed class SandboxJsonExecutor(SandboxAgentHost host) : IAgentJsonExecutor
{
    public async Task<string> ExecuteAsync(SandboxAgentRequest request, CancellationToken ct = default)
    {
        if (request.Repos.Count != 0 || request.Repo != null || request.PersistentWorkspaceKey != null)
            throw new ArgumentException("Inference requests require a fresh empty workspace.", nameof(request));
        var session = ClaudeSessionProfiles.Inference(request.Session ?? new() { Tool = request.Tool });
        await using var run = await host.RunAsync(request with
        {
            Session = session, SessionOptions = new() { InteractionMode = InteractionMode.Surface }
        }, ct);
        return await AgentJsonOutput.CollectAsync(run.Session, ct: ct);
    }
}
