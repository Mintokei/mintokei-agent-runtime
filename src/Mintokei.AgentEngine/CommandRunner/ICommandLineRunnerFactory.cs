namespace Mintokei.AgentEngine.CommandRunner;

/// <summary>
/// Factory that creates the appropriate ICommandLineRunner based on
/// whether execution targets a local or remote machine.
/// </summary>
public interface ICommandLineRunnerFactory
{
    /// <summary>
    /// Creates a command line runner for the given target machine.
    /// </summary>
    /// <param name="runnerMachineId">
    /// null for local execution (current machine);
    /// a machine GUID for remote execution via SignalR.
    /// </param>
    ICommandLineRunner Create(Guid? runnerMachineId);
}
