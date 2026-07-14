namespace Mintokei.AgentEngine.CommandRunner;

/// <summary>
/// Interface for running command line processes with streaming output.
/// </summary>
public interface ICommandLineRunner
{
    /// <summary>
    /// Runs a command and streams output lines as they arrive.
    /// The process handle is automatically disposed when enumeration completes.
    /// </summary>
    IAsyncEnumerable<CommandOutput> RunAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a command and returns a handle for interactive control.
    /// Use this overload when you need to write to stdin.
    /// </summary>
    (IProcessHandle Handle, IAsyncEnumerable<CommandOutput> Output) Start(
        CommandLineOptions options,
        CancellationToken cancellationToken = default);
}
