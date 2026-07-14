using Microsoft.Extensions.Logging;
using Mintokei.AgentEngine.AgentTools.Acp;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.AgentEngine.AgentTools.OpenCode;

/// <summary>
/// Discovers OpenCode CLI models via the shared ACP discovery flow
/// (<see cref="AcpModelDiscoveryProviderBase"/>). OpenCode's <c>session/new</c>
/// response includes the same <c>result.models.availableModels</c> shape Copilot uses.
/// </summary>
public sealed class OpenCodeModelDiscoveryProvider : AcpModelDiscoveryProviderBase
{
    private static readonly Lazy<string> ExecutablePath =
        new(() => AcpJsonRpcHelper.ResolveExecutablePath("opencode"));

    public OpenCodeModelDiscoveryProvider(
        ICommandLineRunner runner,
        ILogger<OpenCodeModelDiscoveryProvider> logger)
        : base(runner, logger)
    {
    }

    public override AgentToolKey AgentToolKey => AgentToolKey.OpenCodeCli;

    protected override string ExecutableName => "opencode";

    protected override string ResolveExecutablePath() => ExecutablePath.Value;

    protected override Dictionary<string, string?> AcpLaunchArguments => new()
    {
        ["acp"] = null,
    };
}
