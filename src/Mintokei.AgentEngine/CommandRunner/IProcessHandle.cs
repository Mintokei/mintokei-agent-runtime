namespace Mintokei.AgentEngine.CommandRunner;

/// <summary>
/// Abstraction for controlling a running process, whether local or remote.
/// </summary>
public interface IProcessHandle : IAsyncDisposable
{
    bool HasExited { get; }
    bool IsStdinRedirected { get; }
    int? ExitCode { get; }
    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);
    Task WriteAsync(string text, CancellationToken cancellationToken = default);
    void CloseStdIn();
    Task CloseStdInAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseStdIn();
        return Task.CompletedTask;
    }
    void Cancel();
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}
