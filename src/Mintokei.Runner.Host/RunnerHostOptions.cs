using Mintokei.Runner.Contracts.Messages;

namespace Mintokei.Runner.Host;

/// <summary>
/// Host configuration for the runner data-plane transport (Mintokei.Runner.Host), set via
/// <c>AddRunnerHostCore(o =&gt; ...)</c>. This holds the transport's one <em>pull</em> — the CLI-probe
/// list — as a provider delegate rather than an <see cref="Mintokei.Runner.Host.IRunnerHost"/>
/// method, because it returns a value the handshake sends to the runner (an event can't), and because it
/// is product configuration (Mintokei's is DB-backed and dynamic) rather than a reaction to a transport
/// event. Reactions stay on the optional <see cref="Mintokei.Runner.Host.IRunnerHost"/>.
/// </summary>
public sealed class RunnerHostOptions
{
    /// <summary>
    /// Supplies the CLI probes a connecting runner should run to detect installed CLIs + their versions
    /// (which binaries, how to read the version). Invoked during each runner handshake; may be dynamic
    /// (e.g. DB-backed). Defaults to <em>no probes</em>, so a minimal host that doesn't need CLI/model
    /// discovery leaves it unset — the runner then reports zero CLIs, and task execution is unaffected
    /// (the run path never consults the probe list).
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<CliProbeSpec>>> CliProbesProvider { get; set; }
        = _ => Task.FromResult<IReadOnlyList<CliProbeSpec>>([]);
}
