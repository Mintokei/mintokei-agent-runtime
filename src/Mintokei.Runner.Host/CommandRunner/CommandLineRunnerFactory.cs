using Mintokei.Runner.Host.RemoteExecution;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.Runner.Host.CommandRunner;

/// <summary>
/// Returns a local CommandLineRunner when no machine is specified,
/// or a RemoteCommandLineRunner that dispatches via the durable outbox.
/// </summary>
public sealed class CommandLineRunnerFactory(
    ICommandLineRunner localRunner,
    IRunnerMessageEnqueuer enqueuer,
    RemoteProcessStore remoteProcessStore) : ICommandLineRunnerFactory
{
    public ICommandLineRunner Create(Guid? runnerMachineId)
    {
        if (runnerMachineId is null)
            return localRunner;

        return new RemoteCommandLineRunner(runnerMachineId.Value, enqueuer, remoteProcessStore);
    }
}
