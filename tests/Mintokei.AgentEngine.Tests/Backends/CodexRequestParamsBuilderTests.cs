using System.Text.Json;
using Mintokei.AgentEngine.AgentTools.Codex;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

public class CodexRequestParamsBuilderTests
{
    [Fact]
    public void BuildThreadResumeParams_IncludesFullThreadConfig()
    {
        var config = new CodexConfigMapper.ThreadStartConfig
        {
            Model = "gpt-5",
            ModelProvider = "openai",
            ApprovalPolicy = "on-failure",
            Sandbox = "workspace-write",
            Personality = "pragmatic",
            Ephemeral = true,
            BaseInstructions = "system prompt",
            Config = new CodexConfigMapper.ThreadNestedConfig
            {
                ModelVerbosity = "high",
                WebSearch = "enabled",
                ModelReasoningEffort = "high",
                ModelReasoningSummary = "detailed",
            },
        };

        var threadStart = Serialize(CodexRequestParamsBuilder.BuildThreadStartParams(config));
        var threadResume = Serialize(CodexRequestParamsBuilder.BuildThreadResumeParams("thread-123", config));

        Assert.True(threadStart.GetProperty("ephemeral").GetBoolean());
        Assert.False(threadResume.TryGetProperty("ephemeral", out _));

        Assert.Equal("thread-123", threadResume.GetProperty("threadId").GetString());
        Assert.Equal("gpt-5", threadResume.GetProperty("model").GetString());
        Assert.Equal("openai", threadResume.GetProperty("modelProvider").GetString());
        Assert.Equal("on-failure", threadResume.GetProperty("approvalPolicy").GetString());
        Assert.Equal("workspace-write", threadResume.GetProperty("sandbox").GetString());
        Assert.Equal("pragmatic", threadResume.GetProperty("personality").GetString());
        Assert.Equal("system prompt", threadResume.GetProperty("baseInstructions").GetString());

        var nestedConfig = threadResume.GetProperty("config");
        Assert.Equal("high", nestedConfig.GetProperty("modelVerbosity").GetString());
        Assert.Equal("enabled", nestedConfig.GetProperty("webSearch").GetString());
        Assert.Equal("high", nestedConfig.GetProperty("modelReasoningEffort").GetString());
        Assert.Equal("detailed", nestedConfig.GetProperty("modelReasoningSummary").GetString());
    }

    [Fact]
    public void BuildTurnStartParams_IncludesApprovalAndSandboxOverrides()
    {
        var config = new CodexConfigMapper.TurnStartConfig
        {
            Model = "gpt-5",
            Effort = "high",
            Summary = "concise",
            Personality = "pragmatic",
            ApprovalPolicy = "never",
            SandboxPolicy = CodexConfigMapper.BuildSandboxPolicy("danger-full-access"),
            CollaborationMode = "plan",
        };
        var input = new List<object> { new { type = "text", text = "hello" } };

        var turnStart = Serialize(CodexRequestParamsBuilder.BuildTurnStartParams("thread-123", input, config));

        Assert.Equal("thread-123", turnStart.GetProperty("threadId").GetString());
        Assert.Equal("gpt-5", turnStart.GetProperty("model").GetString());
        Assert.Equal("high", turnStart.GetProperty("effort").GetString());
        Assert.Equal("concise", turnStart.GetProperty("summary").GetString());
        Assert.Equal("pragmatic", turnStart.GetProperty("personality").GetString());
        Assert.Equal("never", turnStart.GetProperty("approvalPolicy").GetString());
        Assert.Equal("dangerFullAccess", turnStart.GetProperty("sandboxPolicy").GetProperty("type").GetString());

        var collaborationMode = turnStart.GetProperty("collaborationMode");
        Assert.Equal("plan", collaborationMode.GetProperty("mode").GetString());
        Assert.Equal("gpt-5", collaborationMode.GetProperty("settings").GetProperty("model").GetString());
        Assert.Equal("high", collaborationMode.GetProperty("settings").GetProperty("reasoning_effort").GetString());

        var inputItems = turnStart.GetProperty("input").EnumerateArray().ToList();
        Assert.Single(inputItems);
        Assert.Equal("hello", inputItems[0].GetProperty("text").GetString());
    }

    private static JsonElement Serialize(object value)
        => JsonDocument.Parse(JsonSerializer.Serialize(value, CodexJsonRpcHelper.JsonOptions)).RootElement.Clone();
}
