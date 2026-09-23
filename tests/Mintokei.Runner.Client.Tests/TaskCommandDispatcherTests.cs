using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mintokei.Runner;
using Xunit;

namespace Mintokei.Runner.Client.Tests;

public sealed class TaskCommandDispatcherTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "runner-commands-" + Guid.NewGuid());
    private LocalOutbox Open() => new(Options.Create(new RunnerOptions { DataDir = directory, BackendUrl = "http://localhost" }));
    public async ValueTask InitializeAsync() { Directory.CreateDirectory(directory); await Open().InitializeAsync(); }
    public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); return ValueTask.CompletedTask; }

    [Fact]
    public async Task Parallel_streams_handle_lower_sequence_launch_and_input_without_duplicate_execution()
    {
        var outbox = Open();
        // Existing installations retain this legacy machine-wide value. It must not
        // suppress a command for another stream when upgrading the runner.
        await outbox.SetLastAckedBackendSequenceAsync(1000);
        var dispatcher = new TaskCommandDispatcher(outbox);
        var release = Guid.NewGuid(); var devops = Guid.NewGuid();
        var started = new HashSet<Guid>(); var input = new List<Guid>(); var acks = new List<long>();
        Task Ack(long seq) { acks.Add(seq); return Task.CompletedTask; }
        Task Start(Guid id) { Assert.True(started.Add(id)); return Task.CompletedTask; }
        await dispatcher.DispatchAsync(devops, 292, () => Start(devops), () => Ack(292));
        await dispatcher.DispatchAsync(release, 290, () => Start(release), () => Ack(290));
        await dispatcher.DispatchAsync(release, 291, () => { Assert.Contains(release, started); input.Add(release); return Task.CompletedTask; }, () => Ack(291));
        // Reconnect / restart: a repeated Start must only be acknowledged.
        dispatcher = new TaskCommandDispatcher(Open());
        Assert.Equal(291, await Open().GetLastProcessedTaskSequenceAsync(release));
        Assert.Equal(292, await Open().GetLastProcessedTaskSequenceAsync(devops));
        await dispatcher.DispatchAsync(release, 290, () => Start(release), () => Ack(290));
        Assert.Equal(2, started.Count); Assert.Single(input);
        Assert.Equal(new long[] { 292, 290, 291, 290 }, acks);
    }

    [Fact]
    public async Task Failed_command_blocks_only_its_stream_until_replayed_even_after_restart()
    {
        var dispatcher = new TaskCommandDispatcher(Open()); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var handled = new List<long>(); var acked = new List<long>();
        Task Handle(long seq) { handled.Add(seq); return Task.CompletedTask; }
        Task Ack(long seq) { acked.Add(seq); return Task.CompletedTask; }
        await Assert.ThrowsAsync<IOException>(() => dispatcher.DispatchAsync(a, 10, () => throw new IOException("launch failed"), () => Ack(10)));
        dispatcher = new TaskCommandDispatcher(Open());
        Assert.Equal(0, await Open().GetLastProcessedTaskSequenceAsync(a));
        Assert.False(await dispatcher.DispatchAsync(a, 12, () => Handle(12), () => Ack(12)));
        Assert.True(await dispatcher.DispatchAsync(b, 13, () => Handle(13), () => Ack(13)));
        Assert.True(await dispatcher.DispatchAsync(a, 10, () => Handle(10), () => Ack(10)));
        Assert.True(await dispatcher.DispatchAsync(a, 12, () => Handle(12), () => Ack(12)));
        Assert.Equal(new long[] { 13, 10, 12 }, handled);
        Assert.Equal(handled, acked);
    }

    [Fact]
    public async Task Lost_ack_replays_receipt_without_repeating_handler()
    {
        var id = Guid.NewGuid(); var calls = 0;
        Task Execute() { calls++; return Task.CompletedTask; }
        await Assert.ThrowsAsync<IOException>(() => new TaskCommandDispatcher(Open()).DispatchAsync(id, 5, Execute, () => throw new IOException("disconnected")));
        Assert.True(await new TaskCommandDispatcher(Open()).DispatchAsync(id, 5, Execute, () => Task.CompletedTask));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Independent_processes_are_not_serialized_while_handler_is_busy()
    {
        var dispatcher = new TaskCommandDispatcher(Open());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = dispatcher.DispatchAsync(Guid.NewGuid(), 20, async () => { entered.SetResult(); await release.Task; }, () => Task.CompletedTask);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(await dispatcher.DispatchAsync(Guid.NewGuid(), 21, () => Task.CompletedTask, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { release.TrySetResult(); await first; }
    }
}
