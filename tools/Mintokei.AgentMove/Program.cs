using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentMove;
using Mintokei.AgentTranscripts;
using Mintokei.AgentTranscripts.Claude;
using Mintokei.AgentTranscripts.Codex;
using Mintokei.AgentTranscripts.Copilot;

// agentmove — pick a session from one agent CLI and carry on with it in another.
//
// The whole point is that neither half is guesswork: the sessions come from each CLI's own store,
// and the target's launch settings come from a profile you wrote down, not from flags assembled
// under time pressure.

var options = MoveOptions.Parse(args);
if (options.ShowHelp)
{
    PrintUsage();
    return 0;
}

if (options.Init)
    return WriteStarterConfig(options.ConfigPath);

MoveConfig config;
string origin;
try
{
    (config, origin) = MoveConfig.Load(options.ConfigPath);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"config: {ex.Message}");
    return 1;
}

var cwd = options.WorkingDirectory;
Console.WriteLine($"directory  {cwd}");
Console.WriteLine($"config     {origin}");
Console.WriteLine();

// ── 1. which agent to copy from ──────────────────────────────────────────

var sources = new (AgentToolKey Key, string Name, ITranscriptStore Store)[]
{
    (AgentToolKey.ClaudeCodeCli, "Claude Code", new ClaudeTranscriptStore()),
    (AgentToolKey.CodexCli, "Codex", new CodexTranscriptStore()),
    (AgentToolKey.GithubCopilotCli, "GitHub Copilot CLI", new CopilotTranscriptStore()),
};

var available = new List<(string Name, ITranscriptStore Store, List<StoredTranscriptInfo> Sessions)>();
foreach (var (_, name, store) in sources)
{
    var found = new List<StoredTranscriptInfo>();
    try
    {
        await foreach (var s in store.ListAsync(cwd))
            found.Add(s);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"  ({name}: could not read its store — {ex.Message})");
    }
    if (found.Count > 0)
        available.Add((name, store, found));
}

if (available.Count == 0)
{
    Console.Error.WriteLine($"No sessions found for {cwd}.");
    Console.Error.WriteLine("Only Claude Code, Codex and GitHub Copilot CLI have transcript stores so far.");
    return 1;
}

var source = options.From is { } wanted
    ? available.FirstOrDefault(a => a.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
    : default;

if (source.Store is null)
{
    if (options.From is not null)
    {
        Console.Error.WriteLine($"No sessions from '{options.From}' in this directory.");
        return 1;
    }
    if (available.Count == 1)
    {
        source = available[0];
        Console.WriteLine($"Source: {source.Name} (the only one with sessions here)");
    }
    else
    {
        Console.WriteLine("Copy from:");
        for (var i = 0; i < available.Count; i++)
            Console.WriteLine($"  [{i + 1}] {available[i].Name}  ({available[i].Sessions.Count} session(s))");
        var choice = Ask("Source", available.Count);
        if (choice is null)
            return 1;
        source = available[choice.Value];
    }
}

// ── 2. which session ─────────────────────────────────────────────────────

var sessions = source.Sessions;
StoredTranscriptInfo picked;

if (options.Session is { } explicitId)
{
    var match = sessions.FirstOrDefault(s => s.SessionId.StartsWith(explicitId, StringComparison.OrdinalIgnoreCase));
    if (match is null)
    {
        Console.Error.WriteLine($"No session starting with '{explicitId}' in this directory.");
        return 1;
    }
    picked = match;
}
else
{
    Console.WriteLine();
    Console.WriteLine($"Sessions in {cwd}:");
    Console.WriteLine();
    var shown = Math.Min(sessions.Count, options.Limit);
    for (var i = 0; i < shown; i++)
    {
        var s = sessions[i];
        // Description before id: nobody recognises a session by its GUID.
        Console.WriteLine($"  [{i + 1,2}] {Ago(s.UpdatedAt),-12} {Describe(s)}");
        Console.WriteLine($"       {Dim(s.SessionId)}");
    }
    if (sessions.Count > shown)
        Console.WriteLine($"       … and {sessions.Count - shown} older (use --limit to see more)");

    var choice = Ask("Session", shown);
    if (choice is null)
        return 1;
    picked = sessions[choice.Value];
}

Console.WriteLine();
Console.WriteLine($"Selected: {Describe(picked)}");

// ── 3. where to continue ─────────────────────────────────────────────────

if (config.Profiles.Count == 0)
{
    Console.Error.WriteLine("No profiles configured. Run `agentmove --init` to write a starter config.");
    return 1;
}

var profileNames = config.Profiles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
string profileName;

if (options.To is { } wantedProfile)
{
    profileName = profileNames.FirstOrDefault(
        n => n.Equals(wantedProfile, StringComparison.OrdinalIgnoreCase))
        ?? throw Bail($"No profile named '{wantedProfile}'. Have: {string.Join(", ", profileNames)}");
}
else
{
    Console.WriteLine();
    Console.WriteLine("Continue as:");
    for (var i = 0; i < profileNames.Count; i++)
    {
        var p = config.Profiles[profileNames[i]];
        var model = p.Model is { Length: > 0 } ? $"/{p.Model}" : "";
        Console.WriteLine($"  [{i + 1}] {profileNames[i],-14} {p.Tool}{model}"
            + (p.Description is { Length: > 0 } ? $"  — {p.Description}" : ""));
    }
    var choice = Ask("Target", profileNames.Count);
    if (choice is null)
        return 1;
    profileName = profileNames[choice.Value];
}

var profile = config.Profiles[profileName];
AgentToolKey targetKey;
try
{
    targetKey = profile.ToolKey;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"profile '{profileName}': {ex.Message}");
    return 1;
}

var target = StoreFor(targetKey);
if (target is null)
{
    Console.Error.WriteLine($"No transcript store for {targetKey} yet — cannot move a conversation into it.");
    return 1;
}

// A key the target does not understand is dropped by the engine without a word. For a permission
// setting that is the failure that matters: the profile reads as a restriction, the CLI never sees
// one. Refuse rather than move and hope.
var unknown = Backends.Unknown(targetKey, profile.Config);
if (unknown.Count > 0)
{
    Console.Error.WriteLine($"profile '{profileName}' sets keys {profile.Tool} does not understand:");
    foreach (var (key, suggestion) in unknown)
        Console.Error.WriteLine($"  {key}{(suggestion is null ? "" : $"  — did you mean '{suggestion}'?")}");
    Console.Error.WriteLine($"  understood: {string.Join(", ", Backends.AcceptedKeys(targetKey).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}");
    return 1;
}

// ── 4. say what it will do, then do it ───────────────────────────────────

Console.WriteLine();
Console.WriteLine($"  {source.Name}  ->  {profileName} ({profile.Tool}{(profile.Model is { Length: > 0 } ? "/" + profile.Model : "")})");
Console.WriteLine(options.Launch
    ? "  start:       agentmove launches it here"
    : "  start:       a command to run yourself");

// Which of the profile's settings are actually in force depends on that choice, so it is reported
// against it rather than stated once and hoped for.
Reporting.PrintPermissions(targetKey, profile, options.Launch);

if (profile.ExtraArgs.Count > 0)
    Console.WriteLine(options.Launch
        // AgentSessionSpec has no verbatim-arguments field: the engine builds the command line
        // from the config alone, so there is nowhere for these to go.
        ? $"  extra args:  {string.Join(' ', profile.ExtraArgs)}  (NOT applied when launching — "
          + "the engine builds the command line from `config`)"
        : $"  extra args:  {string.Join(' ', profile.ExtraArgs)}");

if (!options.Yes && !Confirm("Proceed?"))
{
    Console.WriteLine("Cancelled.");
    return 1;
}

StoredTranscript read;
try
{
    var loaded = await source.Store.ReadAsync(picked.SessionId);
    if (loaded is null || loaded.Messages.Count == 0)
    {
        Console.Error.WriteLine("That session has nothing transferable in it.");
        return 1;
    }
    read = loaded;
}
catch (TranscriptStoreException ex)
{
    Console.Error.WriteLine($"Could not read the session: {ex.Message}");
    return 1;
}

var trim = read.TrimIncompleteTail();
var transcript = trim.Transcript;
if (trim.DroppedRequest is not null)
    Console.WriteLine($"  trimmed an unfinished turn ({(trim.DroppedUnresolvedToolCall ? "its last step has no recorded result" : "nothing was produced")})");
else if (trim.EndsMidTurn)
    Console.WriteLine("  the last turn was cut off — keeping the work it produced");

if (config.SummariseOver is { } limit && transcript.Messages.Count > limit)
{
    var before = transcript.Messages.Count;
    transcript = transcript.Summarise();
    Console.WriteLine($"  summarised {before} messages into a briefing (over the {limit}-message limit)");
}

if (transcript.Messages.Count == 0)
{
    Console.Error.WriteLine("Nothing left to move.");
    return 1;
}

string newId;
try
{
    newId = await target.WriteAsync(transcript, new TranscriptWriteOptions
    {
        Cwd = cwd,
        Model = profile.Model,
    });
}
catch (TranscriptStoreException ex)
{
    Console.Error.WriteLine($"Could not write the session: {ex.Message}");
    return 1;
}

// A configured template wins. Otherwise: the failure-shaped default only when the conversation
// was actually cut off, and the neutral one when the move was somebody's choice.
var template = config.Handoff ?? (trim.EndsMidTurn ? null : HandoffPrompt.MovedTemplate);
var handoff = HandoffPrompt.Render(template, new HandoffContext
{
    SourceTool = read.Tool,
    TargetTool = targetKey,
    SourceSessionId = picked.SessionId,
    SourcePath = read.SourcePath,
    // Only a turn that was actually cut off leaves something outstanding. This move is
    // deliberate, so there is no failure to report either — both lines drop from the template.
    Request = trim.EndsMidTurn ? trim.OutstandingRequest : null,
    Reason = null,
    Cwd = cwd,
    HasUnresolvedToolCall = trim.EndsMidTurn,
});

Console.WriteLine();
Console.WriteLine($"  moved {transcript.Messages.Count} message(s) as {newId}");

// ── 5. hand it over ──────────────────────────────────────────────────────

if (options.Launch)
{
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        shutdown.Cancel();
    };

    try
    {
        return await Launcher.RunAsync(profile, targetKey, cwd, newId, handoff, shutdown.Token);
    }
    catch (OperationCanceledException)
    {
        // The move already happened; only the conversation that followed it is lost.
        Console.WriteLine();
        Console.WriteLine($"Stopped. The session is still there: {Reporting.ResumeCommand(targetKey, newId, profile)}");
        return 130;
    }
}

Console.WriteLine();
Console.WriteLine($"Resume it with:  {Reporting.ResumeCommand(targetKey, newId, profile)}");

var dropped = Reporting.CommandLine(targetKey, profile).Dropped;
if (dropped.Count > 0)
    Console.WriteLine(
        $"  (that command does not carry {string.Join(", ", dropped)} — "
        + "`--launch` starts the session with the whole profile applied)");

Console.WriteLine();
Console.WriteLine("First thing to say (the history already holds the rest):");
Console.WriteLine();
Console.WriteLine(Indent(handoff));
return 0;

// ── helpers ──────────────────────────────────────────────────────────────

static ITranscriptStore? StoreFor(AgentToolKey tool) => tool switch
{
    AgentToolKey.ClaudeCodeCli => new ClaudeTranscriptStore(),
    AgentToolKey.CodexCli => new CodexTranscriptStore(),
    AgentToolKey.GithubCopilotCli => new CopilotTranscriptStore(),
    _ => null,
};

static string Describe(StoredTranscriptInfo s)
{
    var text = !string.IsNullOrWhiteSpace(s.Title) ? s.Title : s.FirstUserMessage;
    if (string.IsNullOrWhiteSpace(text))
        return "(no description)";
    var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    return flat.Length > 72 ? flat[..71] + "…" : flat;
}

static string Ago(DateTimeOffset when)
{
    if (when == default)
        return "unknown";
    var d = DateTimeOffset.UtcNow - when;
    if (d < TimeSpan.Zero)
        return "just now";
    if (d.TotalMinutes < 1)
        return "just now";
    if (d.TotalHours < 1)
        return $"{(int)d.TotalMinutes}m ago";
    if (d.TotalDays < 1)
        return $"{(int)d.TotalHours}h ago";
    return d.TotalDays < 30 ? $"{(int)d.TotalDays}d ago" : when.ToString("yyyy-MM-dd");
}

static string Dim(string text) =>
    Console.IsOutputRedirected || Environment.GetEnvironmentVariable("NO_COLOR") is not null
        ? text
        : $"[2m{text}[0m";

static string Indent(string text) =>
    string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => "    " + l));

// Returns a zero-based index, or null when the user declined or there is no one to ask.
static int? Ask(string label, int count)
{
    if (count <= 0)
        return null;
    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine(
            $"{label} needed, but stdin is not a terminal. Pass --from/--session/--to to run non-interactively.");
        return null;
    }

    while (true)
    {
        Console.Write($"\n{label} [1-{count}, or q to quit]: ");
        var answer = Console.ReadLine()?.Trim();
        if (answer is null or "q" or "Q")
            return null;
        if (int.TryParse(answer, out var n) && n >= 1 && n <= count)
            return n - 1;
        Console.WriteLine("  not one of the options");
    }
}

static bool Confirm(string question)
{
    if (Console.IsInputRedirected)
        return false;
    Console.Write($"\n{question} [y/N]: ");
    var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
    return answer is "y" or "yes";
}

static Exception Bail(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(1);
    return new InvalidOperationException(message);
}

static int WriteStarterConfig(string? path)
{
    var target = path ?? Path.Combine(Environment.CurrentDirectory, "agentmove.json");
    if (File.Exists(target))
    {
        Console.Error.WriteLine($"{target} already exists — delete it first.");
        return 1;
    }
    File.WriteAllText(target, MoveConfig.Sample + "\n");
    Console.WriteLine($"wrote {target}");
    return 0;
}

static void PrintUsage() => Console.WriteLine("""
    agentmove — pick a session from one agent CLI and carry on with it in another.

    Run it in the directory the work happened in; it lists the sessions each CLI
    recorded there, by description rather than by id.

      agentmove                       pick interactively, print a command to run
      agentmove --launch              pick interactively, then carry on here
      agentmove --init                write a starter agentmove.json

      --dir <path>       directory to look in (default: current)
      --from <cli>       skip the source prompt: claude | codex | copilot
      --session <id>     skip the session prompt (a unique prefix is enough)
      --to <profile>     skip the target prompt
      --launch, -l       start the target CLI here and keep talking to it,
                         instead of printing a command to run yourself
      --limit <n>        how many sessions to list (default 15)
      --config <path>    config file (default: ./agentmove.json, then
                         $XDG_CONFIG_HOME/agentmove/config.json)
      --yes              do not ask for confirmation
      --help

    Profiles say which CLI to continue in and how to launch it. Their `config`
    keys go to the engine's per-backend mappers, so a profile can express
    anything the engine can launch — `model`, `effort`, `permissionMode`
    (Claude), `approvalPolicy` and `access` (Codex), and so on.

    Those settings apply in full only under --launch, because that is the path
    that starts the CLI. A printed command carries what its own resume
    invocation accepts and no more — for Codex that is nothing, since the
    engine drives it over `codex app-server` rather than by flags. agentmove
    says which of the two you are getting before it moves anything.

    Permissions are NOT translated between CLIs, because there is no honest
    mapping. Each profile states its own target's; under --launch the CLI's own
    permission questions are asked here, as they arise.
    """);

internal sealed record MoveOptions(
    string WorkingDirectory,
    string? From,
    string? Session,
    string? To,
    string? ConfigPath,
    int Limit,
    bool Yes,
    bool Init,
    bool Launch,
    bool ShowHelp)
{
    public static MoveOptions Parse(string[] args)
    {
        var dir = Environment.CurrentDirectory;
        string? from = null, session = null, to = null, config = null;
        var limit = 15;
        bool yes = false, init = false, launch = false, help = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h": help = true; break;
                case "--init": init = true; break;
                case "--yes" or "-y": yes = true; break;
                case "--launch" or "-l": launch = true; break;
                case "--dir" when i + 1 < args.Length: dir = Path.GetFullPath(args[++i]); break;
                case "--from" when i + 1 < args.Length: from = args[++i]; break;
                case "--session" when i + 1 < args.Length: session = args[++i]; break;
                case "--to" when i + 1 < args.Length: to = args[++i]; break;
                case "--config" when i + 1 < args.Length: config = args[++i]; break;
                case "--limit" when i + 1 < args.Length: limit = ParseCount(args[++i]); break;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}' (try --help)");
                    Environment.Exit(1);
                    break;
            }
        }

        return new MoveOptions(dir, from, session, to, config, limit, yes, init, launch, help);
    }

    // int.Parse would surface a bad --limit as an unhandled FormatException and a stack trace.
    private static int ParseCount(string raw)
    {
        if (int.TryParse(raw, out var n) && n > 0)
            return n;
        Console.Error.WriteLine($"--limit needs a positive number, not '{raw}'");
        Environment.Exit(1);
        return 0;
    }
}
