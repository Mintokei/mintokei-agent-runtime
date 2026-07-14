using Microsoft.Extensions.Logging;

namespace Mintokei.Runner.Host;

/// <summary>
/// The no-op <see cref="IRunnerHost"/> the transport falls back to when the host registers none.
/// Every <see cref="IRunnerHost"/> member has a default no-op body, so this class implements the
/// interface with no method overrides at all — it just logs once at startup so the disabled reactions
/// are visible rather than silent. <c>AddRunnerHostCore()</c> registers it with <c>TryAdd</c>, so a
/// product that registers its own <see cref="IRunnerHost"/> always wins.
/// </summary>
public sealed class NullRunnerHost : IRunnerHost
{
    public NullRunnerHost(ILogger<NullRunnerHost> logger) =>
        logger.LogInformation(
            "Using the no-op IRunnerHost: runner reconnect reconciliation, installed-CLI persistence, " +
            "remote file-watch re-arm, and orphan cleanup are disabled. Register your own IRunnerHost " +
            "to react to these transport events.");
}
