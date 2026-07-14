using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.Runner.Host.RemoteExecution;

/// <summary>
/// ICommandLineRunner implementation that dispatches process execution to a remote runner
/// via the durable outbox. Output arrives asynchronously through the SignalR hub.
/// </summary>
public sealed class RemoteCommandLineRunner(
    Guid machineId,
    IRunnerMessageEnqueuer enqueuer,
    RemoteProcessStore remoteProcessStore) : ICommandLineRunner
{
    public IAsyncEnumerable<CommandOutput> RunAsync(
        CommandLineOptions options, CancellationToken cancellationToken = default)
    {
        var (handle, output) = Start(options, cancellationToken);
        return output;
    }

    public (IProcessHandle Handle, IAsyncEnumerable<CommandOutput> Output) Start(
        CommandLineOptions options, CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid();

        var remoteHandle = new RemoteProcessHandle(enqueuer, machineId, correlationId);
        remoteProcessStore.Add(correlationId, remoteHandle);

        // Enqueue the start process message — must not be fire-and-forget
        // because the outbox sequence guarantees ordering.
        _ = enqueuer.EnqueueAsync(machineId, OutboxMessageType.StartProcess, new
        {
            CorrelationId = correlationId,
            options.Executable,
            options.Arguments,
            options.ArgumentList,
            options.WorkingDirectory,
            options.EnvironmentVariables,
            options.RedirectStdIn,
            options.CaptureStdErr,
        }, correlationId: correlationId, ct: cancellationToken);

        return (remoteHandle, remoteHandle.GetOutputAsync(cancellationToken));
    }
}
