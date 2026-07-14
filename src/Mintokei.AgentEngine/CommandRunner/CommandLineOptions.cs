namespace Mintokei.AgentEngine.CommandRunner;

/// <summary>
/// Options for executing a command line process.
/// </summary>
public sealed class CommandLineOptions
{
    public required string Executable { get; init; }
    public IReadOnlyDictionary<string, string?>? Arguments { get; init; }

    /// <summary>
    /// Pre-tokenised argv. Takes precedence over <see cref="Arguments"/> when set.
    /// Use this when any value may contain whitespace, newlines, or shell-special
    /// characters — the dictionary form goes through string concatenation and
    /// would be re-split by the OS argv parser.
    /// </summary>
    public IReadOnlyList<string>? ArgumentList { get; init; }

    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
    public bool RedirectStdIn { get; init; }
    public bool CaptureStdErr { get; init; } = true;
}
