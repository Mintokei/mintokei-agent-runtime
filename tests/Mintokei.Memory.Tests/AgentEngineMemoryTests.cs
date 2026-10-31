using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.AgentEngine;
using Mintokei.AgentEngine.Tests;
using Mintokei.Memory.AgentEngine;

namespace Mintokei.Memory.Tests;

public sealed class AgentEngineMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "memory-engine-test-" + Guid.NewGuid());
    private readonly FakeCommandLineRunnerFactory _runner = new();
    private const string Schema = """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""";

    public AgentEngineMemoryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "login"));
        File.WriteAllText(Path.Combine(_root, "login", "auth.json"), "{\"test\":\"not-a-real-credential\"}");
    }

    private AgentEngineLlmProvider Provider() => new(new AgentSessionFactory(_runner, NullLoggerFactory.Instance),
        new MemoryCodexHome(Options.Create(new MemoryEngineOptions
        {
            CodexLoginHome = Path.Combine(_root, "login"),
            RuntimeHome = Path.Combine(_root, "runtime")
        })));

    private LlmRequest Request(int timeout = 5) => new("extraction", new("agent_engine", "test-model", "low", TimeoutSeconds: timeout),
        "untrusted transcript", Schema, Path.Combine(_root, "work"), Guid.NewGuid(), Instructions: "Extract facts", Input: "untrusted transcript");

    private async Task<JsonNode> ReceiveAsync(string method)
        => Json.Node(await _runner.Handle.WaitForWriteAsync(s => Json.Node(s)?["method"]?.ToString() == method, TimeSpan.FromSeconds(5)))!;

    private void Reply(JsonNode request, object result) => _runner.Handle.FeedStdout(Json.Write(new { id = request["id"]!.GetValue<int>(), result }));

    private async Task<JsonNode> HandshakeAsync()
    {
        var initialize = await ReceiveAsync("initialize"); Reply(initialize, new { });
        var thread = await ReceiveAsync("thread/start");
        Assert.Equal("never", thread["params"]!["approvalPolicy"]!.ToString());
        Assert.Equal("read-only", thread["params"]!["sandbox"]!.ToString());
        Assert.True(thread["params"]!["ephemeral"]!.GetValue<bool>());
        Assert.Equal("Extract facts", thread["params"]!["baseInstructions"]!.ToString());
        Reply(thread, new { thread = new { id = "memory-test-thread" } });
        var turn = await ReceiveAsync("turn/start"); Reply(turn, new { turn = new { id = "turn-test" } });
        return turn;
    }

    [Theory]
    [InlineData("{\"ok\":true}", "completed")]
    [InlineData("{\"wrong\":true}", "failed")]
    [InlineData("not JSON", "failed")]
    public async Task Uses_existing_runner_and_schema_validates_observed_answer(string answer, string status)
    {
        var run = Provider().ExecuteStructuredAsync(Request(), default);
        var turn = await HandshakeAsync();
        Assert.Equal("object", turn["params"]!["outputSchema"]!["type"]!.ToString());
        Assert.Equal("test-model", turn["params"]!["model"]!.ToString());
        Assert.Equal("low", turn["params"]!["effort"]!.ToString());
        Assert.False(_runner.LastOptions!.InheritEnvironment);
        Assert.DoesNotContain("OPENAI_API_KEY", _runner.LastOptions.EnvironmentVariables!.Keys);
        Assert.Contains("app-server", _runner.LastOptions.ArgumentList!);
        Assert.Null(_runner.LastMachineId);
        _runner.Handle.FeedStdout(Json.Write(new { method = "item/completed", @params = new { threadId = "memory-test-thread", turnId = "turn-test", item = new { id = "answer", type = "agentMessage", text = answer } } }));
        _runner.Handle.FeedStdout(Json.Write(new { method = "turn/completed", @params = new { threadId = "memory-test-thread", turn = new { id = "turn-test", status = "completed" } } }));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(status, result.Status);
        Assert.Null(result.Usage); Assert.Null(result.ObservedModel);
        Assert.True(_runner.Handle.HasExited);
    }

    [Fact]
    public async Task Timeout_during_handshake_disposes_the_existing_engine_process()
    {
        var result = await Provider().ExecuteStructuredAsync(Request(1), default);
        Assert.Equal("failed", result.Status); Assert.Contains("timed out", result.Error);
        Assert.True(_runner.Handle.HasExited);
    }

    [Fact]
    public async Task Cancellation_during_turn_terminates_the_session()
    {
        using var cancellation = new CancellationTokenSource();
        var run = Provider().ExecuteStructuredAsync(Request(), cancellation.Token);
        await HandshakeAsync(); cancellation.Cancel();
        Assert.Equal("cancelled", (await run.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.True(_runner.Handle.HasExited);
    }

    [Fact]
    public async Task Unsupported_provider_or_write_mode_does_not_start_a_runner()
    {
        var provider = Provider();
        Assert.Equal("failed", (await provider.ExecuteStructuredAsync(Request() with { Config = new(Provider: "openai_api") }, default)).Status);
        Assert.Equal("failed", (await provider.ExecuteStructuredAsync(Request() with { Config = new(ExecutionMode: "workspace_write") }, default)).Status);
        Assert.Null(_runner.LastOptions);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
