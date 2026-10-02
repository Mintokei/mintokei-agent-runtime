using Mintokei.AgentEngine;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.Contracts;
using Xunit;

namespace Mintokei.Sandbox.Hosting.Tests;

public sealed class AgentJsonOutputTests
{
    private static MessageOutput Answer(string content) => new(new()
        { Role = MessageRole.Assistant, Type = MessageType.AgentMessage, Content = content });

    [Fact]
    public async Task Returns_final_answer_without_reasoning_or_tool_transcript()
    {
        var session = new FakeSession();
        session.Script.AddRange([
            new MessageOutput(new() { Role = MessageRole.Assistant, Type = MessageType.Reasoning, Content = "private reasoning" }),
            new MessageOutput(new() { Role = MessageRole.Tool, Type = MessageType.AgentMessage, Content = "tool output" }),
            Answer("intermediate text"), Answer("{\"nodes\":[],\"edges\":[]}"),
            new MessageOutput(new() { Role = MessageRole.Assistant, Type = MessageType.AgentMessage, ParentToolUseId = "child", Content = "subagent" }),
            new TurnEnded(null, false, null)]);
        Assert.Equal("{\"nodes\":[],\"edges\":[]}", await AgentJsonOutput.CollectAsync(session));
    }

    [Fact]
    public async Task Partial_JSON_never_succeeds_on_interruption_error_or_missing_boundary()
    {
        foreach (var ending in new AgentStreamOutput?[]
        {
            null, new TurnEnded(null, true, null), new TurnEnded(null, false, new(TurnFailureKind.RateLimited, "private provider text")),
            new MessageOutput(new() { Type = MessageType.Error, Content = "private provider text", FailureKind = TurnFailureKind.RateLimited })
        })
        {
            var session = new FakeSession(); session.Script.Add(Answer("{}"));
            if (ending != null) session.Script.Add(ending);
            var error = await Assert.ThrowsAsync<AgentInferenceException>(() => AgentJsonOutput.CollectAsync(session));
            Assert.DoesNotContain("private provider", error.ToString());
            if (ending is MessageOutput) Assert.Equal(TurnFailureKind.RateLimited, error.FailureKind);
        }
    }

    [Theory]
    [InlineData("```json\n{}\n```", 100)]
    [InlineData("", 100)]
    [InlineData("{\"long\":true}", 5)]
    public async Task Invalid_or_oversized_results_are_rejected(string content, int limit)
    {
        var session = new FakeSession(); session.Script.AddRange([Answer(content), new TurnEnded(null, false, null)]);
        await Assert.ThrowsAsync<AgentInferenceException>(() => AgentJsonOutput.CollectAsync(session, limit));
    }

    [Fact]
    public async Task Unexpected_permission_is_denied_and_fails_the_operation()
    {
        var session = new FakeSession();
        session.Script.Add(new InteractionRequested("permission", new(), null, null, null, null));
        await Assert.ThrowsAsync<AgentInferenceException>(() => AgentJsonOutput.CollectAsync(session));
        Assert.Equal("deny", Assert.Single(session.Responses).Decision);
    }

    [Fact]
    public void Inference_profile_removes_tools_resume_and_unsafe_extra_configuration()
    {
        var spec = ClaudeSessionProfiles.Inference(new()
        {
            Tool = AgentToolKey.ClaudeCodeCli, EnableMcp = true, McpUrl = "https://tools.example.test", McpToken = "secret",
            ResumeSessionId = "prior", ForkFromSessionId = "prior", ExtraArgs = ["--dangerously-skip-permissions"],
            Config = new() { ["model"] = "chosen", ["permissionMode"] = "bypassPermissions", ["allowedTools"] = "Bash" }
        });
        Assert.False(spec.EnableMcp); Assert.Null(spec.McpUrl); Assert.Null(spec.McpToken); Assert.Null(spec.ResumeSessionId);
        Assert.Null(spec.ForkFromSessionId); Assert.Equal("chosen", spec.Config!["model"]);
        Assert.Equal("default", spec.Config["permissionMode"]); Assert.False(spec.Config.ContainsKey("allowedTools"));
        Assert.DoesNotContain("--dangerously-skip-permissions", spec.ExtraArgs!);
        Assert.Contains("--restricted", spec.ExtraArgs!);
        Assert.Equal("", spec.ExtraArgs![Array.IndexOf(spec.ExtraArgs.ToArray(), "--tools") + 1]);
        Assert.Throws<NotSupportedException>(() => ClaudeSessionProfiles.Inference(new() { Tool = AgentToolKey.CodexCli }));
    }
}
