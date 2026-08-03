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
SummarySettings summary;
try
{
    (config, origin) = MoveConfig.Load(options.ConfigPath);

    // Resolved here, before a session is even listed, because a summariser naming a profile that
    // does not exist should stop the run while stopping is still free.
    summary = options.ApplyTo(config.EffectiveSummary());
    if (!summary.IsMechanical && !config.Profiles.ContainsKey(summary.With))
    {
        throw new InvalidOperationException(
            $"the summary is set to be written by '{summary.With}', which is not a profile. Use "
            + $"\"{SummarySettings.Mechanical}\", or one of: "
            + string.Join(", ", config.Profiles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));
    }
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

// A key the engine accepts but agentmove cannot deliver would be mapped, sent nowhere, and never
// mentioned. Checked before the unknown-key pass so it gets the accurate reason rather than a
// "did you mean" for a key that is spelled perfectly well.
var inapplicable = profile.Config.Keys
    .Where(k => Backends.Unsupported(k, out _))
    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
    .ToList();
if (inapplicable.Count > 0)
{
    Console.Error.WriteLine($"profile '{profileName}' sets keys that cannot apply to a moved session:");
    foreach (var key in inapplicable)
    {
        Backends.Unsupported(key, out var why);
        Console.Error.WriteLine($"  {key}  — {why}");
    }
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
Console.WriteLine(options.Attach
    ? $"  start:       {profile.Tool}'s own interface, in this terminal"
    : "  start:       a command to run yourself");

var unapplied = Reporting.PrintPermissions(targetKey, profile);

// Printing a warning and then starting the agent anyway is how the widening happens. Only --attach
// starts anything, so it is the one that must stop.
if (options.Attach && unapplied.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine(
        $"--attach cannot apply {string.Join(", ", unapplied)}: {profile.Tool} has no flag for "
        + "these, so the agent would run with its own defaults instead of what this profile says.");
    Console.Error.WriteLine("  Set them in the CLI's own settings and drop them from the profile.");
    return 1;
}

if (profile.ExtraArgs.Count > 0)
    Console.WriteLine($"  extra args:  {string.Join(' ', profile.ExtraArgs)}");

// Said before the confirmation rather than after it, because summarising throws the turn-by-turn
// history away — and because naming a profile starts a second agent in this directory, which is
// not something to discover from the output afterwards.
if (summary.When.When != SummaryWhen.Never)
{
    var trigger = summary.When.When == SummaryWhen.Always
        ? "every session"
        : $"sessions over {summary.When.Threshold} messages";
    Console.WriteLine(summary.IsMechanical
        ? $"  summary:     {trigger}, extracted from the transcript"
        : $"  summary:     {trigger}, written by '{summary.With}'");

    if (!summary.IsMechanical)
    {
        var writer = config.Profiles[summary.With];
        Console.WriteLine($"               {writer.Tool} runs here to read the transcript"
            + $"{(writer.Model is { Length: > 0 } m ? $", as {m}" : "")}");
        if (!Backends.IsReadOnly(writer.ToolKey, writer.Config))
        {
            // The engine refuses every permission request it makes — but a profile that already
            // grants the reach is never asked, so this is the only place anyone sees it.
            Console.WriteLine("               this profile does not pin it to read-only, so it "
                + "may change files here");
        }
    }
}

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

if (summary.When.Applies(transcript.Messages.Count))
{
    var before = transcript.Messages.Count;
    string? narrative = null;

    if (!summary.IsMechanical)
    {
        // The path, not the text: pasting the conversation into the prompt would hit the same
        // context limit that made a summary worth having.
        if (string.IsNullOrWhiteSpace(read.SourcePath))
        {
            Console.WriteLine($"  '{summary.With}' has no transcript file to read — "
                + "using the extracted briefing instead");
        }
        else
        {
            Console.WriteLine($"  asking '{summary.With}' to read the transcript…");
            var attempt = await AgentSummariser.RunAsync(
                config.Profiles[summary.With],
                readable => HandoffPrompt.Render(
                    summary.Prompt ?? SummarySettings.DefaultPrompt,
                    new HandoffContext
                    {
                        SourceTool = read.Tool,
                        TargetTool = targetKey,
                        SourceSessionId = picked.SessionId,
                        // The copy it can actually read, not where the transcript lives.
                        SourcePath = readable,
                        Cwd = cwd,
                    }),
                read.SourcePath,
                cwd,
                TimeSpan.FromSeconds(summary.TimeoutSeconds));

            if (attempt.Briefing is { } written)
                narrative = written;
            else
                Console.WriteLine($"  '{summary.With}' produced nothing ({attempt.Failure}) — "
                    + "using the extracted briefing instead");
        }
    }

    transcript = transcript.Summarise(new SummaryOptions
    {
        Narrative = narrative,
        // Dropping the facts is only ever a choice about what to put under a briefing. With no
        // briefing to put them under, it would leave a summary that says nothing at all.
        IncludeFacts = summary.KeepFacts || narrative is null,
    });
    Console.WriteLine($"  summarised {before} messages into a briefing"
        + (narrative is null ? "" : $", with a handover from '{summary.With}'"));
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

// --no-handoff, or `"handoff": ""` in the config, means send nothing: the session opens with its
// history and whoever picked it up writes the first turn themselves.
// --handoff beats the config file, which beats the built-in wording; --no-handoff beats all three.
var configured = options.Handoff ?? config.Handoff;
var wantsHandoff = !options.NoHandoff && configured is not "";

// A configured template wins. Otherwise: the failure-shaped default only when the conversation
// was actually cut off, and the neutral one when the move was somebody's choice.
var template = configured ?? (trim.EndsMidTurn ? null : HandoffPrompt.MovedTemplate);
var handoff = wantsHandoff
    ? HandoffPrompt.Render(template, new HandoffContext
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
    })
    : null;

Console.WriteLine();
Console.WriteLine($"  moved {transcript.Messages.Count} message(s) as {newId}");

// ── 5. hand it over ──────────────────────────────────────────────────────

if (options.Attach)
{
    if (handoff is null)
    {
        Console.WriteLine($"  starting {profile.Tool} — no opening turn, the history is already there");
    }
    else
    {
        // Shown before the handover, not after: the agent begins working on this the moment the
        // interface opens, and a turn nobody saw coming is a turn nobody agreed to.
        Console.WriteLine();
        Console.WriteLine("  sending as the first turn (--no-handoff to skip):");
        Console.WriteLine();
        Console.WriteLine(Indent(handoff));
        Console.WriteLine();
        Console.WriteLine($"  starting {profile.Tool}…");
    }

    return Attacher.Run(profile, targetKey, cwd, newId, handoff);
}

Console.WriteLine();
Console.WriteLine($"Resume it with:  {Reporting.ResumeCommand(targetKey, newId, profile)}");

if (handoff is not null)
{
    Console.WriteLine();
    Console.WriteLine("First thing to say (the history already holds the rest):");
    Console.WriteLine();
    Console.WriteLine(Indent(handoff));
}

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
      agentmove --attach              …and drop into that CLI's own interface
      agentmove --init                write a starter agentmove.json

      --dir <path>       directory to look in (default: current)
      --from <cli>       skip the source prompt: claude | codex | copilot
      --session <id>     skip the session prompt (a unique prefix is enough)
      --to <profile>     skip the target prompt
      --attach, -a       run the target CLI's real TUI in this terminal, with
                         the profile as flags and the handoff as its first turn
      --handoff <text>   the opening turn: `default`, `minimal`, or any literal
                         template. Beats the config file's "handoff"
      --handoff-file <p> read that template from a file
      --no-handoff       do not send an opening turn: the session opens with
                         its history and you write the first message yourself
      --summarise [n]    replace the conversation with a briefing — always, or
                         only past n messages. Beats the config file's "summary"
      --no-summarise     move the real transcript whatever the config says
      --summarise-with   `mechanical` (extracted, free), or a profile name: that
        <who>            agent reads the transcript and writes the handover
      --summary-prompt   what to ask it for. {sourcePath} and {cwd} are filled in
        <text>
      --summary-prompt-file <p>       read that prompt from a file
      --limit <n>        how many sessions to list (default 15)
      --config <path>    config file (default: ./agentmove.json, then
                         $XDG_CONFIG_HOME/agentmove/config.json)
      --yes              do not ask for confirmation
      --help

    Profiles say which CLI to continue in and how to launch it. Their `config`
    keys go to the engine's per-backend mappers, so a profile can express
    anything the engine can launch — `model`, `effort`, `permissionMode`
    (Claude), `approvalPolicy` and `sandbox` (Codex), and so on. A key its
    backend does not understand is an error, not a shrug.

    A profile becomes arguments to that CLI's own resume invocation, whether
    agentmove prints the command or runs it. A key with no flag form is refused
    up front rather than accepted and dropped, and --attach refuses outright
    rather than start an agent with permissions the profile did not ask for.

    Permissions are NOT translated between CLIs, because there is no honest
    mapping. Each profile states its own target's, and the target asks its own
    questions once it is running.

    Summarising is off by default: moving the real transcript is the point, and a
    briefing is what you reach for when the conversation will not fit. When it is
    a profile writing the briefing, that is a second agent started in this
    directory — agentmove says so, with whether the profile pins it to read-only,
    before it asks you to proceed.
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
    bool Attach,
    bool NoHandoff,
    string? Handoff,
    SummaryTrigger? SummaryWhen,
    string? SummaryWith,
    string? SummaryPrompt,
    bool ShowHelp)
{
    /// <summary>
    /// The config's summary block with this run's flags laid over it. Same precedence as the
    /// handoff flags — the flag beats the file — so there is one rule rather than two.
    /// </summary>
    public SummarySettings ApplyTo(SummarySettings configured) => configured with
    {
        When = SummaryWhen ?? configured.When,
        With = SummaryWith ?? configured.With,
        Prompt = SummaryPrompt ?? configured.Prompt,
    };

    public static MoveOptions Parse(string[] args)
    {
        var dir = Environment.CurrentDirectory;
        string? from = null, session = null, to = null, config = null;
        var limit = 15;
        bool yes = false, init = false, help = false, noHandoff = false, attach = false;
        string? handoff = null;
        SummaryTrigger? summaryWhen = null;
        string? summaryWith = null, summaryPrompt = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h": help = true; break;
                case "--init": init = true; break;
                case "--yes" or "-y": yes = true; break;
                case "--attach" or "-a": attach = true; break;
                case "--no-handoff": noHandoff = true; break;
                case "--handoff" when i + 1 < args.Length:
                    handoff = args[++i] switch
                    {
                        "default" => null,                        // null => the built-in wording
                        "minimal" => HandoffPrompt.MinimalTemplate,
                        var literal => literal,
                    };
                    break;
                case "--handoff-file" when i + 1 < args.Length:
                    handoff = ReadTemplate(args[++i]);
                    break;
                // Both spellings: the config key is `summariseOver` and the method is `Summarise`,
                // but getting an exit code for a regional spelling is a poor way to learn that.
                case "--summarise" or "--summarize":
                    summaryWhen = TakeTrigger(args, ref i);
                    break;
                case "--no-summarise" or "--no-summarize":
                    summaryWhen = SummaryTrigger.Never;
                    break;
                case "--summarise-with" or "--summarize-with" when i + 1 < args.Length:
                    summaryWith = args[++i];
                    break;
                case "--summary-prompt" when i + 1 < args.Length:
                    summaryPrompt = args[++i];
                    break;
                case "--summary-prompt-file" when i + 1 < args.Length:
                    summaryPrompt = ReadTemplate(args[++i]);
                    break;
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

        return new MoveOptions(
            dir, from, session, to, config, limit, yes, init, attach, noHandoff, handoff,
            summaryWhen, summaryWith, summaryPrompt, help);
    }

    /// <summary>
    /// The value after <c>--summarise</c>, when there is one. Bare means "always"; the next token is
    /// only swallowed when it is actually a trigger, so <c>--summarise --yes</c> does not quietly
    /// consume the flag after it and then fail on a word it never meant to parse.
    /// </summary>
    private static SummaryTrigger TakeTrigger(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            return SummaryTrigger.Always;

        try
        {
            var parsed = SummaryTrigger.Parse(args[i + 1]);
            i++;
            return parsed;
        }
        catch (InvalidOperationException)
        {
            return SummaryTrigger.Always;
        }
    }

    private static string ReadTemplate(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"--handoff-file {path}: {ex.Message}");
            Environment.Exit(1);
            return "";
        }
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
