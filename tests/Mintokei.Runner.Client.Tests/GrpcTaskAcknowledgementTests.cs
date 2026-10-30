using System.Threading.Channels;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts.Grpc;
using Xunit;

namespace Mintokei.Runner.Client.Tests;

public class GrpcTaskAcknowledgementTests
{
    [Fact]
    public async Task Open_and_live_acks_retire_only_their_stream_and_preserve_the_sequence_floor()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"outbox-grpc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var outbox = new LocalOutbox(Options.Create(new RunnerOptions { BackendUrl = "http://localhost", DataDir = directory }));
            await outbox.InitializeAsync();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var first = await outbox.InsertAsync("ReportProcessOutput", "a1", a);
            var other = await outbox.InsertAsync("ReportProcessOutput", "b", b);
            var last = await outbox.InsertAsync("ReportProcessCompleted", "a2", a);
            var responses = Channel.CreateUnbounded<TaskServerMessage>();
            responses.Writer.TryWrite(new TaskServerMessage
            {
                OpenAck = new TaskOpenAck { Success = true, LastReceivedRunnerSeq = first },
            });
            var manager = new GrpcTaskStreamManager(NullLogger<GrpcTaskStreamManager>.Instance, outbox);
            manager.RegisterClient(new StubClient(responses), new Metadata());
            Assert.True(await manager.EnsureOpenAsync(a));
            Assert.Equal(new[] { other, last }, (await outbox.GetPendingAsync()).Select(r => r.Seq));

            responses.Writer.TryWrite(new TaskServerMessage { Ack = new Ack { CumulativeSeq = last } });
            for (var attempt = 0; attempt < 100 && (await outbox.GetPendingAsync(200, [a])).Count != 0; attempt++)
                await Task.Delay(10);
            Assert.Empty(await outbox.GetPendingAsync(200, [a]));
            Assert.Equal(other, Assert.Single(await outbox.GetPendingAsync()).Seq);
            Assert.Equal(last + 1, await outbox.InsertAsync("ReportProcessOutput", "next", b));
            await manager.CloseAsync(a);
            for (var attempt = 0; attempt < 100 && manager.IsOpen(a); attempt++)
                await Task.Delay(10);
            Assert.False(manager.IsOpen(a));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StubClient(Channel<TaskServerMessage> responses) : RunnerLink.RunnerLinkClient
    {
        public override AsyncDuplexStreamingCall<TaskClientMessage, TaskServerMessage> OpenTask(
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            => new(new RequestWriter(responses.Writer), new ResponseReader(responses.Reader),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(),
                () => responses.Writer.TryComplete());
    }

    private sealed class RequestWriter(ChannelWriter<TaskServerMessage> responses) : IClientStreamWriter<TaskClientMessage>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(TaskClientMessage message) => Task.CompletedTask;
        public Task CompleteAsync()
        {
            responses.TryComplete();
            return Task.CompletedTask;
        }
    }

    private sealed class ResponseReader(ChannelReader<TaskServerMessage> responses) : IAsyncStreamReader<TaskServerMessage>
    {
        public TaskServerMessage Current { get; private set; } = null!;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!await responses.WaitToReadAsync(cancellationToken)) return false;
            Current = await responses.ReadAsync(cancellationToken);
            return true;
        }
    }
}
