using Grpc.Core;
using Mintokei.Runner.Contracts.Grpc;
using Mintokei.Runner.Host.RemoteExecution.Grpc;
using Xunit;

namespace Mintokei.Runner.Host.Tests;

public class GrpcTaskChannelRegistryTests
{
    [Fact]
    public async Task Output_acks_share_the_command_write_lock()
    {
        var registry = new GrpcTaskChannelRegistry();
        var machine = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var writer = new SerialWriter();
        registry.Register(machine, correlation, writer);

        await Task.WhenAll(
            registry.TrySendAsync(machine, correlation, new ServerTaskCommand { Sequence = 10 }, CancellationToken.None),
            registry.TryAcknowledgeAsync(machine, correlation, 20, CancellationToken.None));
        Assert.Equal(1, writer.MaxConcurrentWrites);
        Assert.Equal(10, Assert.Single(writer.Messages, m => m.Command is not null).Command.Sequence);
        Assert.Equal(20, Assert.Single(writer.Messages, m => m.Ack is not null).Ack.CumulativeSeq);
        Assert.False(await registry.TryAcknowledgeAsync(machine, Guid.NewGuid(), 20, CancellationToken.None));
    }

    private sealed class SerialWriter : IServerStreamWriter<TaskServerMessage>
    {
        private int _writers;
        public int MaxConcurrentWrites { get; private set; }
        public List<TaskServerMessage> Messages { get; } = [];
        public WriteOptions? WriteOptions { get; set; }

        public async Task WriteAsync(TaskServerMessage message)
        {
            var count = Interlocked.Increment(ref _writers);
            MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, count);
            await Task.Yield();
            Messages.Add(message);
            Interlocked.Decrement(ref _writers);
        }
    }
}
