using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Reflection;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts.Grpc;
using Xunit;

namespace Mintokei.Runner.Client.Tests;

public sealed class GrpcTaskReplayTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"grpc-replay-{Guid.NewGuid():N}");
    private LocalOutbox _outbox = null!;
    private GrpcTaskStreamManager _manager = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _outbox = new(Options.Create(new RunnerOptions { BackendUrl = "http://localhost", DataDir = _directory }));
        await _outbox.InitializeAsync();
        _manager = new(NullLogger<GrpcTaskStreamManager>.Instance, _outbox);
    }

    public ValueTask DisposeAsync()
    {
        _manager.UnregisterClient();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Successful_buffered_write_without_receipt_is_replayed_after_disconnect()
    {
        var correlation = Guid.NewGuid();
        var first = await _outbox.InsertAsync("ReportProcessOutput", "first", correlation);
        var final = await _outbox.InsertAsync("ReportProcessOutput", "final answer", correlation);
        var server = new ReplayServer { DropWrites = true };
        _manager.RegisterClient(server, new Metadata());
        Assert.True(await _manager.EnsureOpenAsync(correlation));
        await DrainUntilAsync(() => Task.FromResult(server.Attempted.Count >= 2));
        Assert.Equal(2, (await _outbox.GetPendingAsync()).Count); // writing != acknowledgement
        _manager.UnregisterClient();
        server.DropWrites = false;
        server.Received.TryAdd(first, "first"); // server received this before the disconnect
        _manager.RegisterClient(server, new Metadata());
        await _manager.ReopenPendingAsync();
        var missing = Assert.Single(await _outbox.GetPendingAsync());
        Assert.Equal(final, missing.Seq);
        var replay = new TaskClientMessage { Output = new ProcessOutput { Sequence = final, PayloadJson = missing.Payload } };
        Assert.True(await _manager.TrySendUpAsync(correlation, replay));
        Assert.True(await _manager.TrySendUpAsync(correlation, replay)); // lost ack can cause duplicate transport delivery
        await UntilAsync(async () => (await _outbox.GetPendingAsync()).Count == 0);
        Assert.Equal(new[] { first, final }, server.Received.Keys.Order());
        Assert.Equal("final answer", server.Received[final]);
    }

    [Fact]
    public async Task Completed_process_reopens_without_an_active_process_and_closes_only_after_ack()
    {
        var correlation = Guid.NewGuid();
        var terminal = await _outbox.InsertAsync("ReportProcessCompleted", "done", correlation);
        await _outbox.MarkSentAsync(terminal); // compatibility with the old runner
        var server = new ReplayServer { DropWrites = true };
        _manager.RegisterClient(server, new Metadata());
        await _manager.ReopenPendingAsync(); // no active-process list involved
        Assert.True(_manager.IsOpen(correlation));
        Assert.Equal(terminal, Assert.Single(await _outbox.GetPendingAsync()).Seq);
        var message = new TaskClientMessage { Completed = new ProcessCompleted { Sequence = terminal, PayloadJson = "done" } };
        await DrainUntilAsync(() => Task.FromResult(server.Attempted.Contains(terminal)));
        Assert.True(_manager.IsOpen(correlation)); // no close while completion is only buffered
        _manager.UnregisterClient();
        server.DropWrites = false;
        _manager.RegisterClient(server, new Metadata());
        await _manager.ReopenPendingAsync();
        await DrainUntilAsync(() => Task.FromResult(server.Received.ContainsKey(terminal)));
        await UntilAsync(() => Task.FromResult(!_manager.IsOpen(correlation)));
        Assert.Empty(await _outbox.GetPendingAsync());
        Assert.Empty(await _outbox.GetRecoverableCorrelationsAsync());
        Assert.Equal(terminal, Assert.Single(server.Received).Key);
    }

    [Fact]
    public async Task Completion_received_before_disconnect_is_retired_on_open_ack_without_resending()
    {
        var correlation = Guid.NewGuid();
        var terminal = await _outbox.InsertAsync("ReportProcessCompleted", "done", correlation);
        await _outbox.MarkSentAsync(terminal);
        var server = new ReplayServer();
        server.Received.TryAdd(terminal, "done");
        _manager.RegisterClient(server, new Metadata());
        await _manager.ReopenPendingAsync();
        await UntilAsync(() => Task.FromResult(!_manager.IsOpen(correlation)));
        Assert.Empty(await _outbox.GetPendingAsync());
        Assert.Empty(await _outbox.GetRecoverableCorrelationsAsync());
        Assert.Single(server.Received);
    }

    private async Task DrainUntilAsync(Func<Task<bool>> condition)
    {
        // Exercise the real hosted drain, including its send/ack/close decisions.
        using var service = new RunnerHostedService(Options.Create(new RunnerOptions { BackendUrl = "http://localhost" }),
            null!, _outbox, null!, null!, null!, _manager, null!, null!, null!, null!, null!,
            NullLogger<RunnerHostedService>.Instance);
        using var cts = new CancellationTokenSource();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var loop = (Task)typeof(RunnerHostedService).GetMethod("DrainOutboxLoopAsync", flags)!.Invoke(service, new object[] { cts.Token })!;
        typeof(RunnerHostedService).GetMethod("SignalDrain", flags)!.Invoke(service, null);
        try { await UntilAsync(condition); }
        finally
        {
            cts.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class ReplayServer : RunnerLink.RunnerLinkClient
    {
        public bool DropWrites { get; set; }
        public ConcurrentQueue<long> Attempted { get; } = new();
        public ConcurrentDictionary<long, string> Received { get; } = new();
        public override AsyncDuplexStreamingCall<TaskClientMessage, TaskServerMessage> OpenTask(
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            var replies = Channel.CreateUnbounded<TaskServerMessage>();
            return new(new Writer(this, replies.Writer), new Reader(replies.Reader),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(),
                () => replies.Writer.TryComplete());
        }
        private sealed class Writer(ReplayServer server, ChannelWriter<TaskServerMessage> replies) : IClientStreamWriter<TaskClientMessage>
        {
            public WriteOptions? WriteOptions { get; set; }
            public Task WriteAsync(TaskClientMessage message)
            {
                if (message.Open is not null)
                    replies.TryWrite(new TaskServerMessage { OpenAck = new TaskOpenAck { Success = true, LastReceivedRunnerSeq = server.Received.Keys.DefaultIfEmpty(0).Max() } });
                else
                {
                    var seq = message.Output?.Sequence ?? message.Completed.Sequence;
                    server.Attempted.Enqueue(seq);
                    if (server.DropWrites) return Task.CompletedTask;
                    var payload = message.Output?.PayloadJson ?? message.Completed.PayloadJson;
                    server.Received.TryAdd(seq, payload);
                    replies.TryWrite(new TaskServerMessage { Ack = new Ack { CumulativeSeq = seq } });
                }
                return Task.CompletedTask;
            }
            public Task CompleteAsync() { replies.TryComplete(); return Task.CompletedTask; }
        }
        private sealed class Reader(ChannelReader<TaskServerMessage> replies) : IAsyncStreamReader<TaskServerMessage>
        {
            public TaskServerMessage Current { get; private set; } = null!;
            public async Task<bool> MoveNext(CancellationToken ct)
            {
                if (!await replies.WaitToReadAsync(ct)) return false;
                Current = await replies.ReadAsync(ct); return true;
            }
        }
    }
}
