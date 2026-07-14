namespace Mintokei.AgentEngine.CommandRunner;

/// <summary>
/// The default <see cref="ICommandLineRunnerFactory"/>: always returns a local
/// <see cref="CommandLineRunner"/> that spawns the CLI on the current machine, ignoring the
/// requested machine id. This is all a single-machine host (or a library consumer) needs.
///
/// A host that runs agents on <em>remote</em> machines supplies its own factory that dispatches
/// by <c>runnerMachineId</c>; this local default deliberately doesn't know about that.
/// </summary>
public sealed class LocalCommandLineRunnerFactory : ICommandLineRunnerFactory
{
    private readonly CommandLineRunner _runner = new();

    /// <inheritdoc />
    public ICommandLineRunner Create(Guid? runnerMachineId) => _runner;
}
