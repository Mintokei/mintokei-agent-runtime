using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.Runner.Contracts.Messages;
using Mintokei.Runner.Host.RemoteExecution;
using Mintokei.Runner.Host.Domain.Machines.Enums;
using Xunit;

namespace Mintokei.Runner.Host.Tests;

public sealed class RemoteProcessLifecycleTests
{
    [Fact]
    public async Task Stdin_waits_for_start_and_eof_is_durably_enqueued()
    {
        var enqueuer = new RecordingEnqueuer { BlockStart = true };
        var store = new RemoteProcessStore();
        var runner = new RemoteCommandLineRunner(Guid.NewGuid(), enqueuer, store);
        var (handle, _) = runner.Start(new CommandLineOptions { Executable = "cat", RedirectStdIn = true });
        await using var cleanup = handle;
        var writing = handle.WriteAsync("large prompt");
        Assert.False(writing.IsCompleted);
        Assert.Empty(enqueuer.Messages);
        enqueuer.StartRelease.SetResult();
        await writing;
        await handle.CloseStdInAsync();
        Assert.Equal(new[] { OutboxMessageType.StartProcess, OutboxMessageType.WriteStdin, OutboxMessageType.WriteStdin },
            enqueuer.Messages.Select(m => m.Type));
        Assert.Equal("large prompt", enqueuer.Messages[1].Payload.GetProperty("Text").GetString());
        Assert.True(enqueuer.Messages[2].Payload.GetProperty("Close").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_output_does_not_break_transport_and_disconnected_consumers_can_recover(bool disconnected)
    {
        var store = new RemoteProcessStore();
        var machine = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var enqueuer = new RecordingEnqueuer();
        var stale = new RemoteProcessHandle(enqueuer, machine, correlation);
        store.Add(correlation, stale);
        if (disconnected) stale.MarkDisconnected();
        else await stale.DisposeAsync();
        await using var replacement = new RemoteProcessHandle(enqueuer, machine, correlation);
        var recovery = new Recovery(store, replacement);
        var dispatcher = new RemoteProcessOutputDispatcher(null!, store, recovery, null!, enqueuer, null!,
            NullLogger<RemoteProcessOutputDispatcher>.Instance);
        var report = new ProcessOutputReport(correlation, "late output", "StdOut", DateTimeOffset.UtcNow);
        await dispatcher.DispatchProcessOutputAsync(machine,
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(disconnected ? 1 : 0, recovery.Calls);
        if (disconnected)
        {
            replacement.SetExited(0);
            var received = new List<CommandOutput>();
            await foreach (var item in replacement.GetOutputAsync()) received.Add(item);
            Assert.Equal("late output", Assert.Single(received).Line);
        }
        else Assert.Null(store.Get(correlation));
    }

    private sealed class Recovery(RemoteProcessStore store, RemoteProcessHandle replacement) : IRemoteProcessRecovery
    {
        public int Calls { get; private set; }
        public Task<RemoteProcessHandle?> TryRecoverAsync(Guid correlationId, Guid machineId)
        {
            Calls++;
            store.Add(correlationId, replacement);
            return Task.FromResult<RemoteProcessHandle?>(replacement);
        }
    }

    private sealed class RecordingEnqueuer : IRunnerMessageEnqueuer
    {
        public bool BlockStart { get; init; }
        public TaskCompletionSource StartRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(OutboxMessageType Type, JsonElement Payload)> Messages { get; } = [];
        public async Task<long> EnqueueAsync(Guid machineId, OutboxMessageType type, object payload,
            Guid? correlationId = null, TimeSpan? ttl = null, DateTimeOffset? deliverAfterUtc = null,
            CancellationToken ct = default)
        {
            if (type == OutboxMessageType.StartProcess && BlockStart) await StartRelease.Task.WaitAsync(ct);
            Messages.Add((type, JsonSerializer.SerializeToElement(payload)));
            return Messages.Count;
        }
    }
}
