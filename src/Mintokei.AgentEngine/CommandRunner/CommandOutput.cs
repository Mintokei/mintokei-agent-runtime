namespace Mintokei.AgentEngine.CommandRunner;

/// <summary>
/// Represents a single line of output from a command execution.
/// </summary>
public readonly record struct CommandOutput(
    string Line,
    OutputType Type,
    DateTimeOffset Timestamp);
