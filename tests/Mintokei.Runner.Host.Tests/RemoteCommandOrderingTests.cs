using System.Collections.Concurrent;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.Runner.Host.Domain.Machines.Enums;
using Mintokei.Runner.Host.RemoteExecution;
using Xunit;

namespace Mintokei.Runner.Host.Tests;

public sealed class RemoteCommandOrderingTests
{
    [Fact]
    public async Task Input_and_kill_wait_for_durable_start_enqueue()
    {
        var enqueuer = new DelayedStart();
        var runner = new RemoteCommandLineRunner(Guid.NewGuid(), enqueuer, new RemoteProcessStore());
        var (handle, _) = runner.Start(new CommandLineOptions { Executable = "test-cli" });
        await using var owned = handle;
        var input = handle.WriteLineAsync("initialize");
        handle.Kill();
        Assert.False(input.IsCompleted);
        Assert.Equal(new[] { OutboxMessageType.StartProcess }, enqueuer.Types.ToArray());
        enqueuer.StartPersisted.SetResult(1);
        await input.WaitAsync(TimeSpan.FromSeconds(5));
        await enqueuer.Killed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OutboxMessageType.StartProcess, enqueuer.Types.First());
        Assert.Contains(OutboxMessageType.WriteStdin, enqueuer.Types);
        Assert.Contains(OutboxMessageType.KillProcess, enqueuer.Types);
    }

    private sealed class DelayedStart : IRunnerMessageEnqueuer
    {
        public ConcurrentQueue<OutboxMessageType> Types { get; } = new();
        public TaskCompletionSource<long> StartPersisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Killed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<long> EnqueueAsync(Guid machineId, OutboxMessageType type, object payload,
            Guid? correlationId = null, TimeSpan? ttl = null, DateTimeOffset? deliverAfterUtc = null, CancellationToken ct = default)
        {
            Types.Enqueue(type);
            if (type == OutboxMessageType.StartProcess) return StartPersisted.Task;
            if (type == OutboxMessageType.KillProcess) Killed.TrySetResult();
            return Task.FromResult(2L);
        }
    }
}
