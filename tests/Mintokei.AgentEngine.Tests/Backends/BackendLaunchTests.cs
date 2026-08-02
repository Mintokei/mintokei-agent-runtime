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
        Assert.Equal("app-server", opts.ArgumentList![0]);

        var cflag = opts.ArgumentList[opts.ArgumentList.ToList().IndexOf("-c") + 1];
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
        Assert.DoesNotContain("-c", opts.ArgumentList!);
        Assert.Equal("true", opts.EnvironmentVariables!["MINTOKEI_MCP_DISABLED"]);
    }

    [Fact]
    public void Codex_suppresses_project_docs_by_config_field_not_a_flag()
    {
        // `codex app-server --no-project-doc` is rejected outright — "unexpected argument" — which
        // killed the session at launch rather than skipping AGENTS.md.
        var opts = new CodexBackend().BuildCommandLine(new AgentSessionSpec
        {
            Config = new Dictionary<string, string?> { ["noProjectDoc"] = "true" },
        });

        Assert.DoesNotContain("--no-project-doc", opts.ArgumentList!);
        Assert.Contains("project_doc_max_bytes=0", opts.ArgumentList!);
    }

    [Fact]
    public void Codex_can_carry_two_config_overrides_at_once()
    {
        // The dictionary form held one value per flag, so MCP and the project-doc override could
        // not both be expressed.
        var opts = new CodexBackend().BuildCommandLine(new AgentSessionSpec
        {
            EnableMcp = true,
            McpUrl = "http://host/mcp",
            McpToken = "t",
            Config = new Dictionary<string, string?> { ["noProjectDoc"] = "true" },
        });

        Assert.Equal(2, opts.ArgumentList!.Count(a => a == "-c"));
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
