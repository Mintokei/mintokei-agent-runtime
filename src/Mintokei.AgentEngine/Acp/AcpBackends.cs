using Microsoft.Extensions.Logging;
using Mintokei.AgentEngine.Acp;
using Mintokei.AgentEngine.Copilot;
using Mintokei.AgentEngine.OpenCode;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.CommandRunner;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Acp;

/// <summary>
/// Shared ACP backend scaffold (Copilot + OpenCode). Both speak the same protocol, so
/// <see cref="CreateProtocol"/> and the reply builder are common; MCP is a <em>protocol</em> concern
/// for ACP (the <c>mcpServers</c> array passed to <c>session/new|load</c>), not a launch flag — so it
/// goes into the protocol, never the command line. Subclasses differ only in the launch (executable +
/// args) and OpenCode's per-turn model threading.
/// </summary>
public abstract class AcpBackendBase : IAgentBackend
{
    public abstract AgentToolKey Tool { get; }
    public abstract CommandLineOptions BuildCommandLine(AgentSessionSpec spec);

    public IInteractionReplyBuilder ReplyBuilder { get; } = new AcpInteractionReplyBuilder();

    public IAgentSessionProtocol CreateProtocol(AgentSessionSpec spec, ILogger logger)
        => new AcpSessionProtocol(logger, spec.WorkingDirectory, BuildMcpServers(spec), BuildPromptParams(spec));

    /// <summary>Per-turn prompt-params shape; default <c>{sessionId, prompt}</c> (Copilot). OpenCode overrides.</summary>
    protected virtual Func<string, IReadOnlyList<object>, object>? BuildPromptParams(AgentSessionSpec spec) => null;

    /// <summary>The <c>mcpServers</c> array for <c>session/new|load</c> — Mintokei's MCP as an http
    /// transport with a bearer header, or empty when no token (MCP off). Mirrors
    /// <c>AcpExecutionServiceBase.BuildMcpServers</c>.</summary>
    private static object BuildMcpServers(AgentSessionSpec spec)
    {
        if (!spec.EnableMcp)
            return Array.Empty<object>();

        return new object[]
        {
            new
            {
                type = "http",
                name = "mintokei",
                url = spec.McpUrl,
                headers = new[] { new { name = "Authorization", value = $"Bearer {spec.McpToken}" } },
            },
        };
    }

    /// <summary>Base env: the adapter's vars plus <c>MINTOKEI_MCP_DISABLED</c> when MCP is off.</summary>
    private protected static Dictionary<string, string> BuildEnv(AgentSessionSpec spec, IReadOnlyDictionary<string, string>? staticEnv = null)
    {
        var env = new Dictionary<string, string>();
        if (staticEnv is not null)
        {
            foreach (var (key, value) in staticEnv)
                env[key] = value;
        }
        if (spec.EnvironmentVariables is { } extra)
        {
            foreach (var (key, value) in extra)
                env[key] = value;
        }
        if (!spec.EnableMcp)
            env["MINTOKEI_MCP_DISABLED"] = "true";
        return env;
    }
}

/// <summary>GitHub Copilot CLI over ACP (<c>copilot --acp</c>).</summary>
public sealed class CopilotBackend : AcpBackendBase
{
    public override AgentToolKey Tool => AgentToolKey.GithubCopilotCli;

    public override CommandLineOptions BuildCommandLine(AgentSessionSpec spec)
    {
        var arguments = CopilotCliConfigMapper.ToCliArguments(
            CopilotCliConfigMapper.Map(spec.Config ?? new Dictionary<string, string?>()));

        if (!string.IsNullOrEmpty(spec.WorkingDirectory))
            arguments["--add-dir"] = spec.WorkingDirectory;

        return new CommandLineOptions
        {
            Executable = "copilot",
            Arguments = arguments,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStdIn = true,
            CaptureStdErr = true,
            EnvironmentVariables = BuildEnv(spec, new Dictionary<string, string> { ["COPILOT_AUTO_UPDATE"] = "false" }),
        };
    }
}

/// <summary>OpenCode CLI over ACP (<c>opencode acp</c> — subcommand-first argv).</summary>
public sealed class OpenCodeBackend : AcpBackendBase
{
    public override AgentToolKey Tool => AgentToolKey.OpenCodeCli;

    public override CommandLineOptions BuildCommandLine(AgentSessionSpec spec)
    {
        var argList = OpenCodeCliConfigMapper.ToArgumentList(
            OpenCodeCliConfigMapper.Map(spec.Config ?? new Dictionary<string, string?>()), spec.WorkingDirectory);

        return new CommandLineOptions
        {
            Executable = "opencode",
            ArgumentList = argList,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStdIn = true,
            CaptureStdErr = true,
            EnvironmentVariables = BuildEnv(spec),
        };
    }

    // Thread the configured model through every session/prompt via _meta.opencode.modelId.
    protected override Func<string, IReadOnlyList<object>, object>? BuildPromptParams(AgentSessionSpec spec)
    {
        var modelId = spec.Config?.GetValueOrDefault("model");
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        return (sessionId, prompt) => new { sessionId, prompt, _meta = new { opencode = new { modelId } } };
    }
}
