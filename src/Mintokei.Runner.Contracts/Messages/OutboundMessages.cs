namespace Mintokei.Runner.Contracts.Messages;

/// <summary>
/// Envelope wrapping every backend→runner message sent over the wire.
/// </summary>
public sealed record OutboxMessageEnvelope(
    Guid MessageId,
    long SequenceNumber,
    string MessageType,
    string PayloadJson);

/// <summary>
/// Request to start a new process on the runner.
/// </summary>
public sealed record StartProcessMessage(
    Guid CorrelationId,
    string Executable,
    Dictionary<string, string?>? Arguments,
    string? WorkingDirectory,
    Dictionary<string, string>? EnvironmentVariables,
    bool RedirectStdIn,
    bool CaptureStdErr,
    List<string>? ArgumentList = null);

/// <summary>
/// Request to write text to stdin of a running process.
/// </summary>
public sealed record WriteStdinMessage(
    Guid CorrelationId,
    string Text,
    bool AppendNewline);

/// <summary>
/// Request to kill a running process.
/// </summary>
public sealed record KillProcessMessage(
    Guid CorrelationId);

/// <summary>
/// Heartbeat ping from backend to runner.
/// </summary>
public sealed record HeartbeatPingMessage(
    DateTimeOffset SentAt);
