using Mintokei.AgentEngine.Claude;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Golden tests for <see cref="ClaudeBackend.BuildCommandLine"/> — the launch builder extracted from
/// <c>ClaudeCodeExecutionService.BuildCliOptions</c>. Pure over the spec, so unit-testable here (the
/// prod path only adds the DB/auth-derived spec fields, which this pins the assembly of).
/// </summary>
public class ClaudeBackendTests
{
    private static readonly ClaudeBackend Backend = new();

    [Fact]
    public void Sets_the_stream_json_launch_and_disables_mcp_without_a_token()
    {
        var opts = Backend.BuildCommandLine(new AgentSessionSpec
        {
            WorkingDirectory = "/work",
            EnvironmentVariables = new Dictionary<string, string> { ["MINTOKEI_WORKSPACE_ID"] = "ws-1" },
        });

        Assert.Equal("claude", opts.Executable);
        Assert.True(opts.RedirectStdIn);
        Assert.Equal("/work", opts.WorkingDirectory);

        var args = opts.Arguments!;
        Assert.Equal("stream-json", args["--output-format"]);
        Assert.Equal("stream-json", args["--input-format"]);
        Assert.True(args.ContainsKey("--verbose"));
        Assert.Equal("stdio", args["--permission-prompt-tool"]);
        Assert.True(args.ContainsKey("--replay-user-messages"));
        Assert.True(args.ContainsKey("--include-partial-messages"));
        Assert.False(args.ContainsKey("--mcp-config"));

        var env = opts.EnvironmentVariables!;
        Assert.Equal("true", env["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"]);
        Assert.Equal("ws-1", env["MINTOKEI_WORKSPACE_ID"]);    // adapter env merged in
        Assert.Equal("true", env["MINTOKEI_MCP_DISABLED"]);    // no token → MCP disabled
    }

    [Fact]
    public void Wires_the_mcp_config_when_a_token_is_present()
    {
        var opts = Backend.BuildCommandLine(new AgentSessionSpec
        {
            EnableMcp = true,
            McpUrl = "http://host/mcp/agent-tasks/x",
            McpToken = "tok-123",
        });

        var mcp = opts.Arguments!["--mcp-config"];
        Assert.NotNull(mcp);
        Assert.Contains("http://host/mcp/agent-tasks/x", mcp);
        Assert.Contains("Bearer tok-123", mcp);
        Assert.False(opts.EnvironmentVariables!.ContainsKey("MINTOKEI_MCP_DISABLED"));
    }

    [Fact]
    public void Maps_resume_fork_and_config_flags()
    {
        var resume = Backend.BuildCommandLine(new AgentSessionSpec { ResumeSessionId = "sess-1" });
        Assert.Equal("sess-1", resume.Arguments!["--resume"]);
        Assert.False(resume.Arguments.ContainsKey("--fork-session"));

        // Fork overrides --resume with the source id and adds --fork-session.
        var fork = Backend.BuildCommandLine(new AgentSessionSpec { ResumeSessionId = "sess-1", ForkFromSessionId = "src-9" });
        Assert.Equal("src-9", fork.Arguments!["--resume"]);
        Assert.True(fork.Arguments.ContainsKey("--fork-session"));

        // Config maps through ClaudeCodeConfigMapper (model → --model).
        var model = Backend.BuildCommandLine(new AgentSessionSpec { Config = new Dictionary<string, string?> { ["model"] = "claude-x" } });
        Assert.Equal("claude-x", model.Arguments!["--model"]);
    }
}
