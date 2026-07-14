using System.Diagnostics;

namespace Mintokei.AgentEngine.CommandRunner;

/// <summary>
/// Handle to a running local process, providing stdin access and control.
/// </summary>
public sealed class ProcessHandle : IProcessHandle
{
    private readonly Process _process;
    private readonly CancellationTokenSource _cts;

    internal ProcessHandle(Process process, CancellationTokenSource cts)
    {
        _process = process;
        _cts = cts;
    }

    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;
    public bool IsStdinRedirected => _process.StartInfo.RedirectStandardInput;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        if (!_process.StartInfo.RedirectStandardInput)
            throw new InvalidOperationException("Stdin was not redirected.");

        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!_process.StartInfo.RedirectStandardInput)
            throw new InvalidOperationException("Stdin was not redirected.");

        await _process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    public void CloseStdIn()
    {
        if (_process.StartInfo.RedirectStandardInput)
            _process.StandardInput.Close();
    }

    public void Cancel() => _cts.Cancel();

    public void Kill()
    {
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        => _process.WaitForExitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        try { await _process.WaitForExitAsync(); } catch { }
        _process.Dispose();
        _cts.Dispose();
    }
}
