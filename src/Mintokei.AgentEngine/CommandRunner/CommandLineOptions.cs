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

    /// <summary>Resolve both argument forms and extras to literal argv tokens. Used before remote
    /// dispatch so empty values, quotes and newlines have the same meaning on either side.</summary>
    public IReadOnlyList<string> ToArgumentList()
    {
        var args = ArgumentList is { Count: > 0 } ? ArgumentList.ToList() : [];
        if (ArgumentList is not { Count: > 0 })
            foreach (var (key, value) in Arguments ?? new Dictionary<string, string?>())
            {
                args.Add(key);
                if (value is not null) args.Add(value);
            }
        if (ExtraArgs is not null) args.AddRange(ExtraArgs);
        return args;
    }
}
