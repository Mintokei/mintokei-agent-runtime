using System.Text.Json;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.Runner.Host.Domain.Machines.Enums;
using Mintokei.Runner.Host.RemoteExecution;

using Xunit;

namespace Mintokei.Runner.Host.Tests;

public sealed class RemoteArgumentTests
{
    [Fact]
    public async Task Native_flags_and_empty_settings_value_survive_remote_dispatch()
    {
        var queue = new CaptureQueue();
        var store = new RemoteProcessStore();
        var remote = new RemoteCommandLineRunner(Guid.NewGuid(), queue, store);
        var options = new CommandLineOptions
        {
            Executable = "claude", Arguments = new Dictionary<string, string?> { ["--verbose"] = null, ["--append-system-prompt"] = "Two lines\nand \"quotes\"" },
            ExtraArgs = ["--no-session-persistence", "--setting-sources", "", "--settings", "{\"autoMemoryEnabled\":false}"],
            WorkingDirectory = "/workspace", RedirectStdIn = true,
            EnvironmentVariables = new Dictionary<string, string> { ["APPLICATION_RUN_ID"] = "test" }
        };
        var (handle, _) = remote.Start(options);
        await handle.WriteLineAsync("initialize");
        var argv = queue.Start.GetProperty("ArgumentList").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "--verbose", "--append-system-prompt", "Two lines\nand \"quotes\"", "--no-session-persistence", "--setting-sources", "", "--settings", "{\"autoMemoryEnabled\":false}" }, argv);
        Assert.Equal("/workspace", queue.Start.GetProperty("WorkingDirectory").GetString());
        Assert.True(queue.Start.GetProperty("RedirectStdIn").GetBoolean());
        Assert.Equal("test", queue.Start.GetProperty("EnvironmentVariables").GetProperty("APPLICATION_RUN_ID").GetString());
        await handle.DisposeAsync();
    }

    [Fact]
    public void Existing_argv_takes_precedence_and_extras_are_applied_once()
    {
        var options = new CommandLineOptions { Executable = "codex", Arguments = new Dictionary<string, string?> { ["ignored"] = null }, ArgumentList = ["app-server"], ExtraArgs = ["-c", "model_provider=\"example\""] };
        var result = options.ToArgumentList();
        Assert.Equal(new[] { "app-server", "-c", "model_provider=\"example\"" }, result);
        Assert.Equal(new[] { "app-server" }, options.ArgumentList); // Resolving never mutates the input.
        Assert.Equal(result, options.ToArgumentList());
    }

    private sealed class CaptureQueue : IRunnerMessageEnqueuer
    {
        public JsonElement Start { get; private set; }
        public Task<long> EnqueueAsync(Guid machineId, OutboxMessageType type, object payload, Guid? correlationId = null,
            TimeSpan? ttl = null, DateTimeOffset? deliverAfterUtc = null, CancellationToken ct = default)
        {
            if (type == OutboxMessageType.StartProcess) Start = JsonSerializer.SerializeToElement(payload);
            return Task.FromResult(1L);
        }
    }
}
