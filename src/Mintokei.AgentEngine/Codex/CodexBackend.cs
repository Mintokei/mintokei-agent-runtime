using Microsoft.Extensions.Logging;
using Mintokei.AgentEngine.Codex;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.CommandRunner;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Codex;

/// <summary>
/// Codex app-server backend module: launch (<c>codex app-server</c>) + protocol. Mirrors
/// <c>CodexAppServerExecutionService.BuildCliOptions</c>, now pure over the spec. Codex wires MCP via
/// a <c>-c</c> config flag plus the <c>MINTOKEI_TOKEN</c> env var (not <c>--mcp-config</c> JSON like Claude).
/// </summary>
public sealed class CodexBackend : IAgentBackend
{
    public AgentToolKey Tool => AgentToolKey.CodexCli;

    public IInteractionReplyBuilder ReplyBuilder { get; } = new CodexInteractionReplyBuilder();

    public IAgentSessionProtocol CreateProtocol(AgentSessionSpec spec, ILogger logger)
        => new CodexSessionProtocol(logger, CodexConfigMapper.Map(spec.Config ?? new Dictionary<string, string?>()), spec.SystemPrompt);

    public CommandLineOptions BuildCommandLine(AgentSessionSpec spec)
    {
        var mapped = CodexConfigMapper.Map(spec.Config ?? new Dictionary<string, string?>());

        var arguments = new Dictionary<string, string?> { ["app-server"] = null };
        if (mapped.Cli.NoProjectDoc)
            arguments["--no-project-doc"] = null;

        var envVars = new Dictionary<string, string>();
        if (spec.EnvironmentVariables is { } extra)
        {
            foreach (var (key, value) in extra)
                envVars[key] = value;
        }

        if (spec.EnableMcp)
        {
            arguments["-c"] = $"mcp_servers.mintokei={{url=\"{spec.McpUrl}\",bearer_token_env_var=\"MINTOKEI_TOKEN\"}}";
            envVars["MINTOKEI_TOKEN"] = spec.McpToken ?? "";
        }
        else
        {
            envVars["MINTOKEI_MCP_DISABLED"] = "true";
        }

        return new CommandLineOptions
        {
            Executable = "codex",
            Arguments = arguments,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStdIn = true,
            CaptureStdErr = true,
            EnvironmentVariables = envVars,
        };
    }
}
