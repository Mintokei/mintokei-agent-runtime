using System.Text.Json;
using Xunit;

namespace Mintokei.AgentEngine.Tests;

public class CodexThreadItemParserTests
{
    private static readonly Guid TaskId = Guid.NewGuid();

    private static JsonElement Json(object obj)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement.Clone();
    }

    // --- agentMessage ---

    [Fact]
    public void Parse_AgentMessage_ReturnsMappedEntity()
    {
        var item = Json(new { type = "agentMessage", id = "msg-1", text = "Hello world" });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal(TaskId, result.AgentTaskId);
        Assert.Equal("msg-1", result.ExternalId);
        Assert.Equal(MessageRole.Assistant, result.Role);
        Assert.Equal(MessageType.AgentMessage, result.Type);
        Assert.Equal("Hello world", result.Content);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    // --- plan ---

    [Fact]
    public void Parse_Plan_ReturnsMappedEntity()
    {
        var item = Json(new { type = "plan", id = "plan-1", text = "Step 1: do stuff" });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal("plan-1", result.ExternalId);
        Assert.Equal(MessageRole.Assistant, result.Role);
        Assert.Equal(MessageType.Plan, result.Type);
        Assert.Equal("Step 1: do stuff", result.Content);
    }

    // --- reasoning ---

    [Fact]
    public void Parse_Reasoning_JoinsContentAndSummaryArrays()
    {
        var item = Json(new
        {
            type = "reasoning",
            id = "reason-1",
            content = new[] { "Thinking about", "the problem" },
            summary = new[] { "Summary line 1", "Summary line 2" },
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal("reason-1", result.ExternalId);
        Assert.Equal(MessageRole.Assistant, result.Role);
        Assert.Equal(MessageType.Reasoning, result.Type);
        Assert.Equal("Thinking about\nthe problem", result.Content);
        Assert.Equal("Summary line 1\nSummary line 2", result.Metadata);
    }

    [Fact]
    public void Parse_Reasoning_HandlesEmptyArrays()
    {
        var item = Json(new
        {
            type = "reasoning",
            id = "reason-2",
            content = Array.Empty<string>(),
            summary = Array.Empty<string>(),
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Content);
        Assert.Equal(string.Empty, result.Metadata);
    }

    // --- commandExecution ---

    [Fact]
    public void Parse_CommandExecution_ReturnsMappedEntityWithChild()
    {
        var item = Json(new
        {
            type = "commandExecution",
            id = "cmd-1",
            status = "completed",
            durationMs = 1500,
            command = "ls -la",
            cwd = "/home/user",
            exitCode = 0,
            aggregatedOutput = "total 42\ndrwxr-xr-x ...",
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal("cmd-1", result.ExternalId);
        Assert.Equal(MessageRole.Tool, result.Role);
        Assert.Equal(MessageType.CommandExecution, result.Type);
        Assert.Equal(MessageStatus.Completed, result.Status);
        Assert.Equal(1500, result.DurationMs);

        Assert.NotNull(result.CommandExecution);
        Assert.Equal("ls -la", result.CommandExecution.Command);
        Assert.Equal("/home/user", result.CommandExecution.Cwd);
        Assert.Equal(0, result.CommandExecution.ExitCode);
        Assert.Equal("total 42\ndrwxr-xr-x ...", result.CommandExecution.Output);
    }

    [Fact]
    public void Parse_CommandExecution_FailedStatus()
    {
        var item = Json(new
        {
            type = "commandExecution",
            id = "cmd-2",
            status = "failed",
            command = "exit 1",
            cwd = "/tmp",
            exitCode = 1,
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal(MessageStatus.Failed, result.Status);
        Assert.NotNull(result.CommandExecution);
        Assert.Equal(1, result.CommandExecution.ExitCode);
        Assert.Null(result.CommandExecution.Output);
    }

    // --- fileChange ---

    [Fact]
    public void Parse_FileChange_ReturnsMappedEntityWithChildren()
    {
        var item = Json(new
        {
            type = "fileChange",
            id = "fc-1",
            status = "completed",
            changes = new[]
            {
                new { path = "src/main.cs", diff = "+line1\n-line2", kind = new { type = "update" } },
                new { path = "src/new.cs", diff = "+new file", kind = new { type = "add" } },
                new { path = "src/old.cs", diff = "-deleted", kind = new { type = "delete" } },
            },
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal("fc-1", result.ExternalId);
        Assert.Equal(MessageRole.Tool, result.Role);
        Assert.Equal(MessageType.FileChange, result.Type);
        Assert.Equal(MessageStatus.Completed, result.Status);
        Assert.Equal(3, result.FileChanges.Count);

        var changes = result.FileChanges.ToList();

        Assert.Equal("src/main.cs", changes[0].Path);
        Assert.Equal("+line1\n-line2", changes[0].Diff);
        Assert.Equal(Mintokei.AgentEngine.Contracts.FileChangeKind.Update, changes[0].ChangeKind);

        Assert.Equal("src/new.cs", changes[1].Path);
        Assert.Equal(Mintokei.AgentEngine.Contracts.FileChangeKind.Add, changes[1].ChangeKind);

        Assert.Equal("src/old.cs", changes[2].Path);
        Assert.Equal(Mintokei.AgentEngine.Contracts.FileChangeKind.Delete, changes[2].ChangeKind);
    }

    [Fact]
    public void Parse_FileChange_Add_RawFileBody_IsNormalizedToPureAddHunk()
    {
        // Codex emits adds as the raw file body (no `@@` header, no `+` prefix) — the chat diff
        // renderer needs a real hunk for Shiki to tokenize, so we wrap it here.
        var item = Json(new
        {
            type = "fileChange",
            id = "fc-add",
            status = "completed",
            changes = new[]
            {
                new { path = "Person.cs", diff = "using System;\n\nnamespace P;\n", kind = new { type = "add" } },
            },
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);
        var change = Assert.Single(result!.FileChanges);
        Assert.Equal(Mintokei.AgentEngine.Contracts.FileChangeKind.Add, change.ChangeKind);
        Assert.Equal(
            "@@ -0,0 +1,3 @@\n+using System;\n+\n+namespace P;",
            change.Diff);
    }

    [Fact]
    public void Parse_FileChange_Update_UnifiedDiff_PassesThroughUnchanged()
    {
        // Updates already arrive as proper unified diffs with `@@ -a,b +c,d @@` — don't touch them.
        var unifiedDiff = "@@ -1,2 +1,2 @@\n-foo\n+bar\n context";
        var item = Json(new
        {
            type = "fileChange",
            id = "fc-upd",
            status = "completed",
            changes = new[]
            {
                new { path = "file.cs", diff = unifiedDiff, kind = new { type = "update" } },
            },
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);
        Assert.Equal(unifiedDiff, Assert.Single(result!.FileChanges).Diff);
    }

    [Fact]
    public void Parse_FileChange_EmptyChangesArray()
    {
        var item = Json(new
        {
            type = "fileChange",
            id = "fc-2",
            status = "completed",
            changes = Array.Empty<object>(),
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Empty(result.FileChanges);
    }

    // --- mcpToolCall ---

    [Fact]
    public void Parse_McpToolCall_ReturnsMappedEntityWithChild()
    {
        var item = Json(new
        {
            type = "mcpToolCall",
            id = "mcp-1",
            status = "completed",
            durationMs = 320,
            tool = "read_file",
            server = "filesystem",
            args = new { path = "/etc/hosts" },
            result = new { content = "127.0.0.1 localhost" },
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal("mcp-1", result.ExternalId);
        Assert.Equal(MessageRole.Tool, result.Role);
        Assert.Equal(MessageType.ToolCall, result.Type);
        Assert.Equal(MessageStatus.Completed, result.Status);
        Assert.Equal(320, result.DurationMs);

        Assert.NotNull(result.ToolCall);
        Assert.Equal("read_file", result.ToolCall.ToolName);
        Assert.Equal("filesystem", result.ToolCall.ServerName);
        Assert.NotNull(result.ToolCall.Arguments);
        Assert.Contains("/etc/hosts", result.ToolCall.Arguments);
        Assert.NotNull(result.ToolCall.Result);
        Assert.Contains("localhost", result.ToolCall.Result);
        Assert.Null(result.ToolCall.Error);
    }

    [Fact]
    public void Parse_McpToolCall_WithError()
    {
        var item = Json(new
        {
            type = "mcpToolCall",
            id = "mcp-2",
            status = "failed",
            tool = "bad_tool",
            server = "test-server",
            error = new { message = "Tool not found" },
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal(MessageStatus.Failed, result.Status);
        Assert.NotNull(result.ToolCall);
        Assert.Equal("Tool not found", result.ToolCall.Error);
    }

    // --- dynamicToolCall ---

    [Fact]
    public void Parse_DynamicToolCall_ServerNameIsNull()
    {
        var item = Json(new
        {
            type = "dynamicToolCall",
            id = "dyn-1",
            status = "completed",
            durationMs = 100,
            tool = "custom_tool",
            args = new { key = "value" },
            contentItems = new[] { new { type = "text", text = "result" } },
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal("dyn-1", result.ExternalId);
        Assert.Equal(MessageRole.Tool, result.Role);
        Assert.Equal(MessageType.ToolCall, result.Type);
        Assert.Equal(100, result.DurationMs);

        Assert.NotNull(result.ToolCall);
        Assert.Equal("custom_tool", result.ToolCall.ToolName);
        Assert.Null(result.ToolCall.ServerName);
        Assert.NotNull(result.ToolCall.Result);
        Assert.Contains("result", result.ToolCall.Result);
    }

    // --- webSearch ---

    [Fact]
    public void Parse_WebSearch_ReturnsMappedEntity()
    {
        var item = Json(new { type = "webSearch", id = "ws-1", query = "how to parse JSON" });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal("ws-1", result.ExternalId);
        Assert.Equal(MessageRole.Assistant, result.Role);
        Assert.Equal(MessageType.WebSearch, result.Type);
        Assert.Equal("how to parse JSON", result.Content);
    }

    // --- skipped types ---

    [Fact]
    public void Parse_UserMessage_ReturnsNull()
    {
        var item = Json(new { type = "userMessage", id = "um-1", text = "hi" });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("imageView")]
    [InlineData("reviewMode")]
    [InlineData("collabAgent")]
    [InlineData("someFutureType")]
    public void Parse_UnknownTypes_ReturnsNull(string type)
    {
        var item = Json(new { type, id = "x" });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.Null(result);
    }

    // --- edge cases ---

    [Fact]
    public void Parse_MissingTypeProperty_ReturnsNull()
    {
        var item = Json(new { id = "no-type", text = "oops" });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.Null(result);
    }

    [Fact]
    public void Parse_MalformedItem_ReturnsNullInsteadOfThrowing()
    {
        // An agentMessage item where 'text' is a number instead of string — should still not throw
        var item = Json(new { type = "agentMessage", id = "bad-1", text = 42 });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        // Should return a message (text will be null since it's not a string)
        Assert.NotNull(result);
        Assert.Null(result.Content);
    }

    [Fact]
    public void Parse_SetsCreatedAt()
    {
        var before = DateTimeOffset.UtcNow;
        var item = Json(new { type = "agentMessage", id = "ts-1", text = "hello" });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.True(result.CreatedAt >= before);
        Assert.True(result.CreatedAt <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void Parse_GeneratesUniqueIds()
    {
        var item = Json(new { type = "agentMessage", id = "dup", text = "test" });

        var result1 = CodexThreadItemParser.Parse(TaskId, item);
        var result2 = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.NotEqual(result1.Id, result2.Id);
    }

    // --- status mapping ---

    [Theory]
    [InlineData("inProgress", MessageStatus.InProgress)]
    [InlineData("completed", MessageStatus.Completed)]
    [InlineData("failed", MessageStatus.Failed)]
    [InlineData("declined", MessageStatus.Declined)]
    public void Parse_CommandExecution_MapsStatusCorrectly(string protocolStatus, MessageStatus expected)
    {
        var item = Json(new
        {
            type = "commandExecution",
            id = "status-test",
            status = protocolStatus,
            command = "echo test",
            cwd = "/tmp",
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void Parse_CommandExecution_UnknownStatus_ReturnsNull()
    {
        var item = Json(new
        {
            type = "commandExecution",
            id = "status-test",
            status = "unknown_status",
            command = "echo test",
            cwd = "/tmp",
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Null(result.Status);
    }

    // --- fileChange kind mapping ---

    [Fact]
    public void Parse_FileChange_UnknownKindDefaultsToUpdate()
    {
        var item = Json(new
        {
            type = "fileChange",
            id = "fc-kind",
            status = "completed",
            changes = new[]
            {
                new { path = "file.txt", diff = "diff", kind = new { type = "rename" } },
            },
        });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        var change = Assert.Single(result.FileChanges);
        Assert.Equal(Mintokei.AgentEngine.Contracts.FileChangeKind.Update, change.ChangeKind);
    }

    [Fact]
    public void Parse_FileChange_MissingKind_DefaultsToUpdate()
    {
        // kind property absent entirely — use raw JSON to omit it
        var json = """{"type":"fileChange","id":"fc-nk","status":"completed","changes":[{"path":"a.txt","diff":"d"}]}""";
        var item = JsonDocument.Parse(json).RootElement.Clone();

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        var change = Assert.Single(result.FileChanges);
        Assert.Equal(Mintokei.AgentEngine.Contracts.FileChangeKind.Update, change.ChangeKind);
    }

    // --- missing optional fields ---

    [Fact]
    public void Parse_CommandExecution_MissingOptionalFields()
    {
        var json = """{"type":"commandExecution","id":"cmd-min","status":"inProgress","command":"echo hi","cwd":"/tmp"}""";
        var item = JsonDocument.Parse(json).RootElement.Clone();

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Null(result.DurationMs);
        Assert.NotNull(result.CommandExecution);
        Assert.Null(result.CommandExecution.ExitCode);
        Assert.Null(result.CommandExecution.Output);
    }

    [Fact]
    public void Parse_McpToolCall_MissingOptionalFields()
    {
        var json = """{"type":"mcpToolCall","id":"mcp-min","status":"inProgress","tool":"my_tool","server":"srv"}""";
        var item = JsonDocument.Parse(json).RootElement.Clone();

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Null(result.DurationMs);
        Assert.NotNull(result.ToolCall);
        Assert.Null(result.ToolCall.Arguments);
        Assert.Null(result.ToolCall.Result);
        Assert.Null(result.ToolCall.Error);
    }

    // --- contextCompaction ---

    [Fact]
    public void Parse_ContextCompaction_ReturnsBoundaryWithAutoTrigger()
    {
        // Schema from codex app-server v2: ContextCompactionThreadItem only carries {id, type}.
        // Summary, tokens, duration are not exposed — the summary is encrypted in the rollout.
        var item = Json(new { type = "contextCompaction", id = "compact-1" });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Equal(TaskId, result.AgentTaskId);
        Assert.Equal("compact-1", result.ExternalId);
        Assert.Equal(MessageRole.System, result.Role);
        Assert.Equal(MessageType.CompactBoundary, result.Type);
        Assert.Equal(MessageStatus.Completed, result.Status);
        Assert.NotNull(result.CompactBoundary);
        Assert.Equal(Mintokei.AgentEngine.Contracts.CompactTrigger.Auto, result.CompactBoundary.Trigger);
        Assert.True(result.CompactBoundary.Success);
        Assert.Null(result.CompactBoundary.PreTokens);
        Assert.Null(result.CompactBoundary.PostTokens);
        Assert.Null(result.CompactBoundary.DurationMs);
        Assert.Null(result.CompactBoundary.SummaryText);
        Assert.Null(result.CompactBoundary.ToolsBeforeJson);
    }

    [Fact]
    public void Parse_ContextCompaction_DirectHelperCall_ReturnsSameShape()
    {
        var item = Json(new { type = "contextCompaction", id = "compact-2" });

        var result = CodexThreadItemParser.ParseContextCompaction(TaskId, item);

        Assert.Equal(MessageType.CompactBoundary, result.Type);
        Assert.Equal("compact-2", result.ExternalId);
        Assert.Equal(Mintokei.AgentEngine.Contracts.CompactTrigger.Auto, result.CompactBoundary!.Trigger);
    }

    [Fact]
    public void Parse_ContextCompaction_MissingId_ReturnsNullExternalId()
    {
        var item = Json(new { type = "contextCompaction" });

        var result = CodexThreadItemParser.Parse(TaskId, item);

        Assert.NotNull(result);
        Assert.Null(result.ExternalId);
        Assert.Equal(MessageType.CompactBoundary, result.Type);
    }
}
