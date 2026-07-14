using Microsoft.Extensions.Logging;
using Mintokei.AgentEngine.AgentTools.Acp;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.AgentEngine.AgentTools.Copilot;

/// <summary>
/// Discovers GitHub Copilot CLI models via the shared ACP discovery flow
/// (<see cref="AcpModelDiscoveryProviderBase"/>).
/// </summary>
public sealed class CopilotModelDiscoveryProvider : AcpModelDiscoveryProviderBase
{
    private static readonly Lazy<string> ExecutablePath =
        new(() => AcpJsonRpcHelper.ResolveExecutablePath("copilot"));

    public CopilotModelDiscoveryProvider(
        ICommandLineRunner runner,
        ILogger<CopilotModelDiscoveryProvider> logger)
        : base(runner, logger)
    {
    }

    public override AgentToolKey AgentToolKey => AgentToolKey.GithubCopilotCli;

    protected override string ExecutableName => "copilot";

    protected override string ResolveExecutablePath() => ExecutablePath.Value;

    protected override Dictionary<string, string?> AcpLaunchArguments => new()
    {
        ["--acp"] = null,
        ["--no-auto-update"] = null,
    };
}
