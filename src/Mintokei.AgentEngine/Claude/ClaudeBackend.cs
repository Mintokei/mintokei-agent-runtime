using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.CommandRunner;

using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentEngine.Claude;

/// <summary>
/// Claude Code backend module: the single source for launching Claude (<c>--input-format
/// stream-json</c>) and talking to it. <see cref="BuildCommandLine"/> is the former
/// <c>ClaudeCodeExecutionService.BuildCliOptions</c> body, now pure over the spec.
/// </summary>
public sealed class ClaudeBackend : IAgentBackend
{
    public AgentToolKey Tool => AgentToolKey.ClaudeCodeCli;

    public IInteractionReplyBuilder ReplyBuilder { get; } = new ClaudeInteractionReplyBuilder();

    public IAgentSessionProtocol CreateProtocol(AgentSessionSpec spec, ILogger logger)
        => new ClaudeSessionProtocol(logger);

    public CommandLineOptions BuildCommandLine(AgentSessionSpec spec)
    {
        var config = spec.Config ?? new Dictionary<string, string?>();

        var arguments = new Dictionary<string, string?>
        {
            ["--output-format"] = "stream-json",
            ["--input-format"] = "stream-json",
            ["--verbose"] = null,
            ["--permission-prompt-tool"] = "stdio",
            ["--replay-user-messages"] = null,
            ["--include-partial-messages"] = null,
        };
        foreach (var (key, value) in ClaudeCodeConfigMapper.MapToCliArgs(config))
            arguments[key] = value;

        if (!string.IsNullOrWhiteSpace(spec.SystemPrompt))
            arguments["--append-system-prompt"] = spec.SystemPrompt;

        if (!string.IsNullOrEmpty(spec.ResumeSessionId))
            arguments["--resume"] = spec.ResumeSessionId;

        // Fork overwrites --resume with the source id and adds --fork-session (matches the old order).
        if (!string.IsNullOrEmpty(spec.ForkFromSessionId))
        {
            arguments["--resume"] = spec.ForkFromSessionId;
            arguments["--fork-session"] = null;
        }

        if (!string.IsNullOrEmpty(spec.ResumeSessionAt))
            arguments["--resume-session-at"] = spec.ResumeSessionAt;

        var envVars = new Dictionary<string, string>
        {
            ["CLAUDECODE"] = "",
            ["CLAUDE_CODE"] = "",
            ["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"] = "true",
        };
        if (spec.EnvironmentVariables is { } extra)
        {
            foreach (var (key, value) in extra)
                envVars[key] = value;
        }

        // Claude's root guard allows dangerous permission-bypass modes only with an explicit marker.
        if (ClaudeCodeConfigMapper.RequiresSandboxEnvironment(config))
            envVars["IS_SANDBOX"] = "1";

        if (spec.EnableMcp)
        {
            arguments["--mcp-config"] = JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["mintokei"] = new
                    {
                        type = "http",
                        url = spec.McpUrl,
                        headers = new Dictionary<string, string>
                        {
                            ["Authorization"] = $"Bearer {spec.McpToken}",
                        },
                    },
                },
            });
        }
        else
        {
            envVars["MINTOKEI_MCP_DISABLED"] = "true";
        }

        return new CommandLineOptions
        {
            Executable = "claude",
            Arguments = arguments,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStdIn = true,
            CaptureStdErr = true,
            EnvironmentVariables = envVars,
        };
    }
}
