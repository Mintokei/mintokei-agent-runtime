using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mintokei.Runner.Host.Domain.Machines;
using Mintokei.Runner.Host.Persistence;
using Mintokei.Runner.Host.RemoteExecution.Grpc;
using Mintokei.Runner.Host.RemoteExecution;
using Mintokei.Runner.Host.Domain.Machines.Enums;
using Xunit;

namespace Mintokei.Runner.Host.Tests;

public sealed class RunnerHeartbeatWriterTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"heartbeat-{Guid.NewGuid():N}");
    private readonly Guid _machine = Guid.NewGuid();
    private ServiceProvider _services = null!;
    private RunnerHeartbeatWriter _writer = null!;
    private string ConnectionString => $"Data Source={Path.Combine(_directory, "host.db")};Pooling=False;Default Timeout=1";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddDbContext<RunnerHostDbContext>(o => o.UseSqlite(ConnectionString));
        _services = services.BuildServiceProvider();
        _writer = new(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<RunnerHeartbeatWriter>.Instance);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.RunnerMachines.Add(new RunnerMachine { Id = _machine, Name = "test", LastHeartbeatAt = DateTimeOffset.UnixEpoch });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task Released_lock_is_retried_and_heartbeat_persisted()
    {
        await using var blocker = new SqliteConnection(ConnectionString);
        await blocker.OpenAsync();
        using var transaction = blocker.BeginTransaction(deferred: false);
        var write = Task.Run(() => _writer.TryUpdateAsync(_machine, CancellationToken.None));
        await Task.Delay(1300); // first 1s busy timeout, then retry against the released lock
        transaction.Rollback();
        Assert.True(await write);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
        Assert.True((await db.RunnerMachines.SingleAsync()).LastHeartbeatAt > DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Persistent_lock_is_bounded_and_next_heartbeat_can_succeed()
    {
        await using var blocker = new SqliteConnection(ConnectionString);
        await blocker.OpenAsync();
        using var transaction = blocker.BeginTransaction(deferred: false);
        var elapsed = Stopwatch.StartNew();
        Assert.False(await Task.Run(() => _writer.TryUpdateAsync(_machine, CancellationToken.None)));
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 2, 10);
        transaction.Rollback();
        Assert.True(await _writer.TryUpdateAsync(_machine, CancellationToken.None));
    }

    [Fact]
    public async Task Replayed_output_is_deduplicated_before_dispatch()
    {
        var dispatcher = new RemoteProcessOutputDispatcher(
            _services.GetRequiredService<IServiceScopeFactory>(), null!, null!, null!, null!, null!,
            NullLogger<RemoteProcessOutputDispatcher>.Instance);
        Assert.True(await dispatcher.TryRecordInboundAsync(_machine, 1, InboundMessageType.ProcessOutputChunk, "answer"));
        Assert.False(await dispatcher.TryRecordInboundAsync(_machine, 1, InboundMessageType.ProcessOutputChunk, "answer"));
        Assert.True(await dispatcher.TryRecordInboundAsync(_machine, 2, InboundMessageType.ProcessCompleted, "done"));
        Assert.False(await dispatcher.TryRecordInboundAsync(_machine, 2, InboundMessageType.ProcessCompleted, "done"));
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
        Assert.Equal(2, await db.InboundRunnerMessages.CountAsync());
        Assert.Equal(2, (await db.RunnerMachines.SingleAsync()).LastReceivedInboundSequence);
    }

    [Fact]
    public async Task Shutdown_cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _writer.TryUpdateAsync(_machine, cts.Token));
    }
}
