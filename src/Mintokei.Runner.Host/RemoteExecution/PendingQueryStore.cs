using System.Collections.Concurrent;

namespace Mintokei.Runner.Host.RemoteExecution;

/// <summary>
/// Tracks pending request/response queries sent to runners.
/// The hub completes the TCS when the runner responds.
/// </summary>
public sealed class PendingQueryStore
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

    public TaskCompletionSource<string> Create(string requestId, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        // Auto-timeout
        _ = Task.Delay(timeout).ContinueWith(_ =>
        {
            if (_pending.TryRemove(requestId, out var removed))
                removed.TrySetException(new TimeoutException($"Runner query {requestId} timed out."));
        });

        return tcs;
    }

    public bool TryComplete(string requestId, string resultJson)
    {
        if (_pending.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(resultJson);
            return true;
        }
        return false;
    }
}
