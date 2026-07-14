using System.Linq;
using System.Threading.Channels;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// An in-memory <see cref="IProcessHandle"/> for driving an <c>AgentSession</c> without a real CLI.
/// The test feeds stdout/stderr frames via <see cref="FeedStdout"/> / <see cref="FeedStderr"/> and
/// ends the stream with <see cref="CompleteOutput"/> / <see cref="Kill"/>; every stdin line the
/// session writes is captured and can be awaited with <see cref="WaitForWriteAsync"/>.
/// </summary>
internal sealed class FakeProcessHandle : IProcessHandle
{
    private readonly Channel<CommandOutput> _out =
        Channel.CreateUnbounded<CommandOutput>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly List<string> _writes = new();
    private readonly object _gate = new();

    // Re-created on every write and the old one completed, so a WaitForWriteAsync that captured the
    // previous instance before checking the list can't miss a write that lands in the gap.
    private volatile TaskCompletionSource _writeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HasExited { get; private set; }
    public bool IsStdinRedirected => true;
    public int? ExitCode { get; private set; }

    /// <summary>The output stream the session's pump consumes.</summary>
    public IAsyncEnumerable<CommandOutput> Output => _out.Reader.ReadAllAsync();

    public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            _writes.Add(line);
        Interlocked
            .Exchange(ref _writeSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();
        return Task.CompletedTask;
    }

    public Task WriteAsync(string text, CancellationToken cancellationToken = default)
        => WriteLineAsync(text, cancellationToken);

    public void CloseStdIn() { }
    public void Cancel() { }
    public void Kill() { HasExited = true; ExitCode = -1; _out.Writer.TryComplete(); }
    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── test controls ──

    public void FeedStdout(string line)
        => _out.Writer.TryWrite(new CommandOutput(line, OutputType.StdOut, DateTimeOffset.UtcNow));

    public void FeedStderr(string line)
        => _out.Writer.TryWrite(new CommandOutput(line, OutputType.StdErr, DateTimeOffset.UtcNow));

    /// <summary>Ends the stdout stream cleanly (process closed its output, but didn't necessarily exit).</summary>
    public void CompleteOutput() => _out.Writer.TryComplete();

    /// <summary>All stdin lines the session has written so far.</summary>
    public IReadOnlyList<string> Writes
    {
        get { lock (_gate) return _writes.ToArray(); }
    }

    /// <summary>Waits until a written stdin line matches <paramref name="predicate"/>, returning the
    /// most recent match; throws <see cref="TimeoutException"/> if none arrives in time.</summary>
    public async Task<string> WaitForWriteAsync(Func<string, bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var signal = _writeSignal.Task; // capture before checking the list
            string? match;
            lock (_gate)
                match = _writes.LastOrDefault(predicate);
            if (match is not null)
                return match;

            try
            {
                await signal.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"No stdin write matched within {timeout}.");
            }
        }
    }
}
