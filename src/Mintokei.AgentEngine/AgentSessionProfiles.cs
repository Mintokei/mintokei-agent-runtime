using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.Codex;

namespace Mintokei.AgentEngine;

/// <summary>Fresh inference sessions. Hosts supply an empty workspace and an isolated CLI home;
/// account selection, authentication and compute admission remain host responsibilities.</summary>
public static class AgentSessionProfiles
{
    public static AgentSessionSpec Inference(AgentSessionSpec spec) => spec.Tool switch
    {
        AgentToolKey.ClaudeCodeCli => ClaudeSessionProfiles.Inference(spec),
        AgentToolKey.CodexCli => CodexSessionProfiles.Inference(spec),
        _ => throw new NotSupportedException("This CLI has no supported inference-only profile.")
    };
}
