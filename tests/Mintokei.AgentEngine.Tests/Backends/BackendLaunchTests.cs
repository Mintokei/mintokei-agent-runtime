using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentEngine.Acp;
using Mintokei.AgentEngine.Codex;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Golden tests for the Codex / Copilot / OpenCode <see cref="IAgentBackend"/> launch builders
/// (extracted from their execution services), plus the protocol each constructs. Pure over the spec,
/// so unit-testable here. Note ACP wires MCP into the protocol (session/new mcpServers), not the
/// command line; Codex wires it via a -c flag + MINTOKEI_TOKEN env.
/// </summary>
public class BackendLaunchTests
{
    // ── Codex ──

    [Fact]
    public void Codex_launches_the_app_server_and_wires_mcp_via_c_flag_and_token_env()
    {
        var backend = new CodexBackend();

        var opts = backend.BuildCommandLine(new AgentSessionSpec
        {
            WorkingDirectory = "/w",
            EnableMcp = true,
            McpUrl = "http://host/mcp/agent-tasks/x",
            McpToken = "tok-9",
        });

        Assert.Equal("codex", opts.Executable);
        Assert.True(opts.Arguments!.ContainsKey("app-server"));

        var cflag = opts.Arguments["-c"]!;
        Assert.Contains("mcp_servers.mintokei", cflag);
        Assert.Contains("http://host/mcp/agent-tasks/x", cflag);
        Assert.Equal("tok-9", opts.EnvironmentVariables!["MINTOKEI_TOKEN"]);
        Assert.False(opts.EnvironmentVariables.ContainsKey("MINTOKEI_MCP_DISABLED"));

        Assert.IsType<CodexSessionProtocol>(backend.CreateProtocol(new AgentSessionSpec(), NullLogger.Instance));
    }

    [Fact]
    public void Codex_disables_mcp_without_a_token()
    {
        var opts = new CodexBackend().BuildCommandLine(new AgentSessionSpec());
        Assert.False(opts.Arguments!.ContainsKey("-c"));
        Assert.Equal("true", opts.EnvironmentVariables!["MINTOKEI_MCP_DISABLED"]);
    }

    // ── Copilot (ACP) ──

    [Fact]
    public void Copilot_launches_acp_with_add_dir_and_keeps_mcp_out_of_the_command_line()
    {
        var backend = new CopilotBackend();

        var opts = backend.BuildCommandLine(new AgentSessionSpec
        {
            WorkingDirectory = "/repo",
            EnableMcp = true,
            McpUrl = "http://host/mcp",
            McpToken = "tok-1",   // ACP: MCP lives in the protocol, NOT the launch
        });

        Assert.Equal("copilot", opts.Executable);
        Assert.True(opts.Arguments!.ContainsKey("--acp"));
        Assert.Equal("/repo", opts.Arguments["--add-dir"]);
        Assert.Equal("false", opts.EnvironmentVariables!["COPILOT_AUTO_UPDATE"]);
        Assert.False(opts.EnvironmentVariables.ContainsKey("MINTOKEI_MCP_DISABLED"));   // token present
        Assert.DoesNotContain(opts.Arguments, kv => kv.Key.Contains("mcp"));            // MCP not on the command line

        Assert.IsType<AcpSessionProtocol>(backend.CreateProtocol(new AgentSessionSpec(), NullLogger.Instance));
    }

    // ── OpenCode (ACP) ──

    [Fact]
    public void OpenCode_launches_the_acp_subcommand_first_as_argv()
    {
        var backend = new OpenCodeBackend();

        var opts = backend.BuildCommandLine(new AgentSessionSpec { WorkingDirectory = "/repo" });

        Assert.Equal("opencode", opts.Executable);
        Assert.NotNull(opts.ArgumentList);
        Assert.Equal("acp", opts.ArgumentList![0]);      // subcommand MUST be first
        Assert.Contains("/repo", opts.ArgumentList);
        Assert.Equal("true", opts.EnvironmentVariables!["MINTOKEI_MCP_DISABLED"]);   // no token

        Assert.IsType<AcpSessionProtocol>(backend.CreateProtocol(new AgentSessionSpec(), NullLogger.Instance));
    }
}
