namespace Mintokei.Runner.Contracts.Messages;

/// <summary>
/// A chunk of process output (stdout or stderr) from the runner.
/// </summary>
public sealed record ProcessOutputReport(
    Guid CorrelationId,
    string Line,
    string OutputType,
    DateTimeOffset Timestamp);

/// <summary>
/// Notification that a process has exited on the runner.
/// </summary>
public sealed record ProcessCompletedReport(
    Guid CorrelationId,
    int ExitCode,
    DateTimeOffset CompletedAt);

/// <summary>
/// Heartbeat pong response from runner to backend.
/// </summary>
public sealed record HeartbeatPongMessage(
    DateTimeOffset PingSentAt,
    DateTimeOffset PongSentAt);

/// <summary>
/// Runner status update with resource information.
/// </summary>
public sealed record RunnerStatusReport(
    int RunningProcessCount,
    double? CpuUsagePercent,
    long? AvailableMemoryMb,
    DateTimeOffset ReportedAt);
