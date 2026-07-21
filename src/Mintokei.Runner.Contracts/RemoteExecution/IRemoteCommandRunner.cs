using Mintokei.Runner.Contracts.Messages;

namespace Mintokei.Runner.Contracts;

/// <summary>
/// Runs a single command on a specific remote runner (over the gRPC OpenQuery lane) and returns its exit
/// code + stdout/stderr. The argv is passed as a list and encoded for the wire by the implementation
/// (<see cref="RunCommandArgs"/>), so callers never deal with quoting. The runner dials out, so this needs
/// no inbound port; it throws when the runner is not currently connected.
/// </summary>
public interface IRemoteCommandRunner
{
    Task<RunCommandResponse> RunAsync(
        Guid machineId,
        string workingDirectory,
        string executable,
        IReadOnlyList<string> args,
        int timeoutMs,
        CancellationToken ct = default);
}
