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

    /// <summary>
    /// Arguments appended verbatim after everything the backend built — the escape hatch for flags
    /// no config mapper covers. Applied to whichever of the two forms above is in use, so a backend
    /// need only pass them through.
    /// </summary>
    public IReadOnlyList<string>? ExtraArgs { get; init; }

    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
    public bool RedirectStdIn { get; init; }
    public bool CaptureStdErr { get; init; } = true;
}
