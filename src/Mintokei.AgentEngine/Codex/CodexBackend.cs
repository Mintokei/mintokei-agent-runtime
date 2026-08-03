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

        // Tokenised rather than the dictionary form because `-c` may need to appear more than once
        // — a dictionary can hold one value per flag, and both the project-doc override and the MCP
        // server want that flag.
        var arguments = new List<string> { "app-server" };

        if (mapped.Cli.NoProjectDoc)
        {
            // NOT `--no-project-doc`: `codex app-server` rejects it outright ("unexpected
            // argument"), taking the whole session down at launch. The config field is the form it
            // does accept, and setting the budget to zero is what suppresses AGENTS.md — verified
            // by the project doc no longer reaching the prompt.
            arguments.Add("-c");
            arguments.Add("project_doc_max_bytes=0");
        }

        var envVars = new Dictionary<string, string>();
        if (spec.EnvironmentVariables is { } extra)
        {
            foreach (var (key, value) in extra)
                envVars[key] = value;
        }

        if (spec.EnableMcp)
        {
            arguments.Add("-c");
            arguments.Add($"mcp_servers.mintokei={{url=\"{spec.McpUrl}\",bearer_token_env_var=\"MINTOKEI_TOKEN\"}}");
            envVars["MINTOKEI_TOKEN"] = spec.McpToken ?? "";
        }
        else
        {
            envVars["MINTOKEI_MCP_DISABLED"] = "true";
        }

        return new CommandLineOptions
        {
            Executable = "codex",
            ArgumentList = arguments,
            ExtraArgs = spec.ExtraArgs,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStdIn = true,
            CaptureStdErr = true,
            EnvironmentVariables = envVars,
        };
    }
}
