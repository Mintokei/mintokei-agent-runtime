using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace Mintokei.Runner.Client.Tests;

public sealed class LocalOutboxTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"outbox-test-{Guid.NewGuid():N}");
    private LocalOutbox _outbox = null!;
    private string ConnectionString => $"Data Source={Path.Combine(_directory, "runner-outbox.db")};Pooling=False";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _outbox = new LocalOutbox(Options.Create(new RunnerOptions { BackendUrl = "http://localhost", DataDir = _directory }));
        await _outbox.InitializeAsync();
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Pending_filter_preserves_order_and_excludes_dead_correlations_and_sent_rows()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await _outbox.InsertAsync("ReportProcessOutput", "dead", Guid.NewGuid());
        var first = await _outbox.InsertAsync("ReportProcessOutput", "a", a);
        var sent = await _outbox.InsertAsync("ReportProcessOutput", "sent", a);
        await _outbox.MarkSentAsync(sent);
        var uncorrelated = await _outbox.InsertAsync("ReportProcessOutput", "null", null);
        var last = await _outbox.InsertAsync("ReportProcessOutput", "b", b);

        Assert.Equal(new[] { first, uncorrelated, last },
            (await _outbox.GetPendingAsync(200, [a, b])).Select(r => r.Seq));
        Assert.Equal(new[] { first, uncorrelated },
            (await _outbox.GetPendingAsync(2, [a, b])).Select(r => r.Seq));
        Assert.Equal(uncorrelated, Assert.Single(await _outbox.GetPendingAsync(200, [])).Seq);
    }

    [Fact]
    public async Task Acknowledgement_does_not_retire_other_streams_or_reuse_sequences()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var first = await _outbox.InsertAsync("ReportProcessOutput", "a", a);
        var last = await _outbox.InsertAsync("ReportProcessOutput", "b", b);
        await _outbox.AcknowledgeAsync(b, last);

        Assert.Equal(first, Assert.Single(await _outbox.GetPendingAsync()).Seq);
        await _outbox.AcknowledgeAsync(a, first);
        await _outbox.AcknowledgeAsync(a, first); // a duplicate, older ack cannot regress the floor
        await _outbox.InitializeAsync();
        Assert.Equal(last + 1, await _outbox.InsertAsync("ReportProcessOutput", "next", a));
    }

    [Fact]
    public async Task Late_mark_sent_cannot_restore_acknowledged_rows_or_delete_later_output()
    {
        var correlation = Guid.NewGuid();
        var first = await _outbox.InsertAsync("ReportProcessOutput", "first", correlation);
        var second = await _outbox.InsertAsync("ReportProcessOutput", "second", correlation);
        await _outbox.AcknowledgeAsync(correlation, first);
        await _outbox.MarkSentAsync(first);
        Assert.Equal(second, Assert.Single(await _outbox.GetPendingAsync()).Seq);
    }

    [Fact]
    public async Task Initialization_upgrades_existing_outbox_without_losing_output()
    {
        var correlation = Guid.NewGuid();
        var seq = await _outbox.InsertAsync("ReportProcessOutput", "saved", correlation);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP INDEX IX_OutboxMessage_Pending_Seq; DROP INDEX IX_OutboxMessage_Pending_Correlation_Seq; DROP INDEX IX_OutboxMessage_Correlation_Seq;";
            await drop.ExecuteNonQueryAsync();
        }
        await _outbox.InitializeAsync();
        await _outbox.InitializeAsync();
        Assert.Equal(seq, Assert.Single(await _outbox.GetPendingAsync(200, [correlation])).Seq);
        // A pending-only ordered index must be available, even on an existing installation.
        await using var plan = conn.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT SequenceNumber FROM OutboxMessage INDEXED BY IX_OutboxMessage_Pending_Seq WHERE Status='Pending' ORDER BY SequenceNumber LIMIT 200";
        await using var reader = await plan.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Contains("IX_OutboxMessage_Pending_Seq", reader.GetString(3));
    }
}
