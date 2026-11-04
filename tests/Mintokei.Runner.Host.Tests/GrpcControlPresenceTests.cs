using System.Security.Claims;
using System.Threading.Channels;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mintokei.AgentControlPlane;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.Runner.Contracts.Grpc;
using Mintokei.Runner.Host.Persistence;
using Mintokei.Runner.Host.Domain.Machines;
using Mintokei.Runner.Host.RemoteExecution;
using Mintokei.Runner.Host.RemoteExecution.Grpc;
using Xunit;

namespace Mintokei.Runner.Host.Tests;

public sealed class GrpcControlPresenceTests : IAsyncLifetime
{
    private readonly Guid _machineId = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly RecordingHost _host = new();
    private ServiceProvider _services = null!;
    private IRunnerRegistry Registry => _services.GetRequiredService<IRunnerRegistry>();
    private GrpcControlChannelRegistry Channels => _services.GetRequiredService<GrpcControlChannelRegistry>();

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<RunnerHostDbContext>(o => o.UseSqlite(_connection));
        services.AddRunnerHostCore();
        services.AddSingleton<ICommandLineRunnerFactory, UnusedRunnerFactory>();
        services.AddAgentControlPlane();
        services.AddSingleton<IRunnerHost>(_host);
        services.AddSingleton<IRemoteProcessRecovery, UnusedRecovery>();
        _services = services.BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.RunnerMachines.Add(new RunnerMachine { Id = _machineId, Name = "runner" });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Current_connection_accepts_heartbeats_and_cleanly_disconnects()
    {
        var stream = await ConnectAsync();
        try
        {
            Assert.True(Registry.IsRunnerConnected(_machineId));
            Assert.True(Channels.IsOpen(_machineId));
            stream.Heartbeat();
            Assert.Equal(ServerControlMessage.PayloadOneofCase.Heartbeat,
                (await stream.ReadAsync()).PayloadCase);
            Assert.True(Registry.IsRunnerConnected(_machineId));
        }
        finally { await stream.CloseAsync(); }

        Assert.False(Registry.IsRunnerConnected(_machineId));
        Assert.False(Channels.IsOpen(_machineId));
        Assert.Equal(1, _host.Disconnects);
    }

    [Fact]
    public async Task Heartbeat_after_timeout_requires_a_new_handshake_and_restores_presence()
    {
        var connectedEvents = 0;
        Registry.RunnerConnected += _ => connectedEvents++;
        var expired = await ConnectAsync();
        var oldConnectionId = Registry.GetConnectionId(_machineId);

        // Same eviction as the product's three-minute heartbeat watchdog. The transport stays open.
        Registry.DisconnectRunner(_machineId);
        Assert.False(Registry.IsRunnerConnected(_machineId));
        Assert.True(Channels.IsOpen(_machineId));
        var heartbeatBefore = await LastHeartbeatAsync();
        expired.Heartbeat();

        var error = await Assert.ThrowsAsync<RpcException>(() => expired.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        Assert.False(Channels.IsOpen(_machineId));
        Assert.Equal(heartbeatBefore, await LastHeartbeatAsync());
        Assert.False(expired.HasReplies);

        // The client retries Unavailable with a fresh Control handshake, rather than continuing
        // to acknowledge heartbeats on an Offline machine indefinitely.
        var reconnected = await ConnectAsync();
        try
        {
            Assert.True(Registry.IsRunnerConnected(_machineId));
            Assert.NotEqual(oldConnectionId, Registry.GetConnectionId(_machineId));
            Assert.Equal(2, connectedEvents);
            Assert.Equal(2, _host.Connects);
            reconnected.Heartbeat();
            Assert.Equal(ServerControlMessage.PayloadOneofCase.Heartbeat,
                (await reconnected.ReadAsync()).PayloadCase);
        }
        finally { await reconnected.CloseAsync(); }
    }

    [Fact]
    public async Task Superseded_stream_cannot_refresh_liveness_or_disconnect_its_replacement()
    {
        var old = await ConnectAsync();
        var replacement = await ConnectAsync();
        var currentConnectionId = Registry.GetConnectionId(_machineId);
        var heartbeatBefore = await LastHeartbeatAsync();
        try
        {
            old.Heartbeat();
            var error = await Assert.ThrowsAsync<RpcException>(() => old.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(StatusCode.Unavailable, error.StatusCode);
            Assert.Equal(heartbeatBefore, await LastHeartbeatAsync());
            Assert.False(old.HasReplies);
            Assert.Equal(currentConnectionId, Registry.GetConnectionId(_machineId));
            Assert.True(Channels.IsOpen(_machineId));
            Assert.Equal(0, _host.Disconnects);
            Assert.NotNull(_services.GetRequiredService<RunnerFileServerPortStore>().GetPort(_machineId));

            replacement.Heartbeat();
            Assert.Equal(ServerControlMessage.PayloadOneofCase.Heartbeat,
                (await replacement.ReadAsync()).PayloadCase);
        }
        finally { await replacement.CloseAsync(); }
    }

    private async Task<DateTimeOffset?> LastHeartbeatAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<RunnerHostDbContext>()
            .RunnerMachines.SingleAsync()).LastHeartbeatAt;
    }

    private async Task<ControlStream> ConnectAsync()
    {
        var stream = new ControlStream();
        var context = new CallContext(_machineId);
        stream.Send(new RunnerControlMessage { Handshake = new HandshakeRequest { FileServerPort = 12345 } });
        stream.Completion = ActivatorUtilities.CreateInstance<RunnerLinkService>(_services)
            .Control(stream, stream, context);
        var handshake = await stream.ReadAsync();
        Assert.True(handshake.Handshake.Success);
        return stream;
    }

    private sealed class RecordingHost : IRunnerHost
    {
        public int Connects { get; private set; }
        public int Disconnects { get; private set; }
        public Task OnRunnerConnectedAsync(Guid machineId, IReadOnlyList<Guid>? activeCorrelationIds)
        {
            Connects++;
            return Task.CompletedTask;
        }
        public Task OnRunnerDisconnectedAsync(Guid machineId)
        {
            Disconnects++;
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedRunnerFactory : ICommandLineRunnerFactory
    {
        public ICommandLineRunner Create(Guid? runnerMachineId) => throw new NotSupportedException();
    }

    private sealed class UnusedRecovery : IRemoteProcessRecovery
    {
        public Task<RemoteProcessHandle?> TryRecoverAsync(Guid correlationId, Guid machineId)
            => throw new NotSupportedException();
    }

    private sealed class ControlStream : IAsyncStreamReader<RunnerControlMessage>, IServerStreamWriter<ServerControlMessage>
    {
        private readonly Channel<RunnerControlMessage> _requests = Channel.CreateUnbounded<RunnerControlMessage>();
        private readonly Channel<ServerControlMessage> _responses = Channel.CreateUnbounded<ServerControlMessage>();
        public Task Completion { get; set; } = Task.CompletedTask;
        public bool HasReplies => _responses.Reader.TryPeek(out _);
        public RunnerControlMessage Current { get; private set; } = null!;
        public WriteOptions? WriteOptions { get; set; }
        public void Send(RunnerControlMessage message) => _requests.Writer.TryWrite(message);
        public void Heartbeat() => Send(new RunnerControlMessage { Heartbeat = new Heartbeat() });
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!await _requests.Reader.WaitToReadAsync(cancellationToken)) return false;
            Current = await _requests.Reader.ReadAsync(cancellationToken);
            return true;
        }
        public Task WriteAsync(ServerControlMessage message) => _responses.Writer.WriteAsync(message).AsTask();
        public Task<ServerControlMessage> ReadAsync() => _responses.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public async Task CloseAsync()
        {
            _requests.Writer.TryComplete();
            await Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        public void Dispose() { }
    }

    private sealed class CallContext : ServerCallContext
    {
        public CallContext(Guid machineId)
        {
            UserState["__HttpContext"] = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("machine_id", machineId.ToString())]))
            };
        }
        protected override string MethodCore => "Control";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore { get; } = new();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore { get; } = new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
            => throw new NotSupportedException();
    }
}
