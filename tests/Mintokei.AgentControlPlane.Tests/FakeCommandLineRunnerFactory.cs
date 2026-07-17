using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.AgentControlPlane.Tests;

/// <summary>
/// A fake <see cref="ICommandLineRunnerFactory"/> that never spawns a real process — its runner
/// hands back a shared <see cref="FakeProcessHandle"/>. Records the last command line and machine id
/// so tests can assert what the launcher asked for (local vs remote, which executable/args).
/// </summary>
internal sealed class FakeCommandLineRunnerFactory : ICommandLineRunnerFactory
{
    public FakeProcessHandle Handle { get; } = new();
    public CommandLineOptions? LastOptions { get; private set; }
    public Guid? LastMachineId { get; private set; }

    public ICommandLineRunner Create(Guid? runnerMachineId)
    {
        LastMachineId = runnerMachineId;
        return new Runner(this);
    }

    private sealed class Runner(FakeCommandLineRunnerFactory parent) : ICommandLineRunner
    {
        public IAsyncEnumerable<CommandOutput> RunAsync(CommandLineOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The launcher uses Start(), not RunAsync().");

        public (IProcessHandle Handle, IAsyncEnumerable<CommandOutput> Output) Start(
            CommandLineOptions options, CancellationToken cancellationToken = default)
        {
            parent.LastOptions = options;
            return (parent.Handle, parent.Handle.Output);
        }
    }
}
