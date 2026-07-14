using System.Text.Json;
using Mintokei.AgentEngine.AgentTools.Codex;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

public class CodexConfigMapperTests
{
    [Fact]
    public void GetConfigFields_ApprovalPolicyAndSandbox_ApplyOnNextTurn()
    {
        var fields = CodexConfigMapper.GetConfigFields().ToDictionary(f => f.Key);

        Assert.Equal(AgentToolConfigApplyMode.NextTurn, fields["approvalPolicy"].ApplyMode);
        Assert.Equal(AgentToolConfigApplyMode.NextTurn, fields["sandbox"].ApplyMode);
    }

    [Fact]
    public void Map_MapsApprovalPolicyAndSandboxIntoTurnConfig()
    {
        var mapped = CodexConfigMapper.Map(new Dictionary<string, string?>
        {
            ["approvalPolicy"] = "on-failure",
            ["sandbox"] = "workspace-write",
        });

        Assert.Equal("on-failure", mapped.ThreadStart.ApprovalPolicy);
        Assert.Equal("on-failure", mapped.TurnStart.ApprovalPolicy);
        Assert.Equal("workspace-write", mapped.ThreadStart.Sandbox);

        var sandboxPolicy = Serialize(mapped.TurnStart.SandboxPolicy);
        Assert.Equal("workspaceWrite", sandboxPolicy.GetProperty("type").GetString());
        Assert.False(sandboxPolicy.GetProperty("networkAccess").GetBoolean());
        Assert.Equal("fullAccess", sandboxPolicy.GetProperty("readOnlyAccess").GetProperty("type").GetString());
        Assert.Empty(sandboxPolicy.GetProperty("writableRoots").EnumerateArray());
    }

    [Theory]
    [InlineData("danger-full-access", "dangerFullAccess")]
    [InlineData("read-only", "readOnly")]
    [InlineData("workspace-write", "workspaceWrite")]
    public void BuildSandboxPolicy_MapsSupportedModes(string sandbox, string expectedType)
    {
        var policy = Serialize(CodexConfigMapper.BuildSandboxPolicy(sandbox));

        Assert.Equal(expectedType, policy.GetProperty("type").GetString());
    }

    [Fact]
    public void BuildSandboxPolicy_UnknownMode_ReturnsNull()
    {
        Assert.Null(CodexConfigMapper.BuildSandboxPolicy("not-a-real-mode"));
    }

    private static JsonElement Serialize(object? value)
        => JsonDocument.Parse(JsonSerializer.Serialize(value, CodexJsonRpcHelper.JsonOptions)).RootElement.Clone();
}
