using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.Runner.Host.RemoteExecution;

/// <summary>
/// Represents a process running on a remote runner machine.
/// Stdin writes are enqueued as outbox messages; stdout/stderr
/// arrive via a channel fed by the SignalR hub.
/// </summary>
public sealed class RemoteProcessHandle : IProcessHandle
{
    private readonly IRunnerMessageEnqueuer _enqueuer;
    private readonly Guid _correlationId;

    public Guid MachineId { get; }
    public Guid CorrelationId => _correlationId;
    private readonly Channel<CommandOutput> _outputChannel;
    private readonly TaskCompletionSource _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();

    private volatile bool _hasExited;
    private volatile bool _disconnected;
    private int? _exitCode;

    public RemoteProcessHandle(
        IRunnerMessageEnqueuer enqueuer,
        Guid machineId,
        Guid correlationId)
    {
        _enqueuer = enqueuer;
        MachineId = machineId;
        _correlationId = correlationId;
        _outputChannel = Channel.CreateUnbounded<CommandOutput>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool HasExited => _hasExited;
    public bool IsStdinRedirected => true;
    public int? ExitCode => _exitCode;

    /// <summary>
    /// True when the runner's transport dropped while this process was tracked —
    /// the output stream is gone, but (unlike <see cref="HasExited"/>) the process
    /// itself is NOT confirmed dead and may still be alive/warm on the runner,
    /// resumable on reconnect.
    /// </summary>
    public bool Disconnected => _disconnected;

    /// <summary>
    /// The channel writer that the RunnerHub uses to feed output from the remote process.
    /// </summary>
    public ChannelWriter<CommandOutput> OutputWriter => _outputChannel.Writer;

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        await _enqueuer.EnqueueAsync(MachineId, OutboxMessageType.WriteStdin,
            new { CorrelationId = _correlationId, Text = line + "\n", AppendNewline = false },
            correlationId: _correlationId, ct: cancellationToken);
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        await _enqueuer.EnqueueAsync(MachineId, OutboxMessageType.WriteStdin,
            new { CorrelationId = _correlationId, Text = text, AppendNewline = false },
            correlationId: _correlationId, ct: cancellationToken);
    }

    public void CloseStdIn()
    {
        // Remote stdin close is a no-op — the runner handles process lifecycle
    }

    public void Cancel()
    {
        _cts.Cancel();
        Kill();
    }

    public void Kill()
    {
        _ = _enqueuer.EnqueueAsync(MachineId, OutboxMessageType.KillProcess,
            new { CorrelationId = _correlationId },
            correlationId: _correlationId);
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        return _exitTcs.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Called by the RunnerHub when the remote process has exited.
    /// </summary>
    public void SetExited(int exitCode)
    {
        _exitCode = exitCode;
        _hasExited = true;
        _outputChannel.Writer.TryComplete();
        _exitTcs.TrySetResult();
    }

    /// <summary>
    /// Called when the runner's transport drops while this process was tracked.
    /// Unblocks the output stream and any exit waiter (so the pump / one-shot
    /// awaiters don't hang) but deliberately does NOT set <see cref="HasExited"/>
    /// or an exit code — the process may still be alive on the runner. HasExited
    /// stays the exclusive signal of a runner-confirmed ProcessCompleted, so
    /// reconnect logic can tell "stream dropped, resume" from "process is dead".
    /// </summary>
    public void MarkDisconnected()
    {
        _disconnected = true;
        _outputChannel.Writer.TryComplete();
        _exitTcs.TrySetResult();
    }

    /// <summary>
    /// Returns the output as an async enumerable backed by the channel.
    /// </summary>
    public async IAsyncEnumerable<CommandOutput> GetOutputAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var output in _outputChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return output;
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _outputChannel.Writer.TryComplete();
        _exitTcs.TrySetResult();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
