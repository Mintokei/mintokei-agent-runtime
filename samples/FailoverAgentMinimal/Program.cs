using Microsoft.Extensions.Logging.Abstractions;

using Mintokei.AgentEngine;
using Mintokei.AgentEngine.Acp;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.Codex;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.AgentEngine.Contracts;
using Mintokei.AgentTranscripts;
using Mintokei.AgentTranscripts.Claude;
using Mintokei.AgentTranscripts.Codex;
using Mintokei.AgentTranscripts.Copilot;

// Runs one prompt against a chain of agent CLIs. When a turn fails for a reason another provider
// could survive — a rate limit, an overloaded or unreachable API, an auth problem — the sample
// moves the conversation to the next entry in the chain and re-sends the turn there.
//
// The two halves come from different packages, which is the point of the sample:
//   Mintokei.AgentEngine      classifies the failure (TurnEnded.Failure.Kind)
//   Mintokei.AgentTranscripts moves the conversation between the CLIs' own stores

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

var options = FailoverOptions.Parse(args);
if (options.ShowHelp)
{
    PrintUsage();
    return 0;
}

Console.WriteLine($"chain: {string.Join("  ->  ", options.Chain.Select(l => l.Describe()))}");
Console.WriteLine($"cwd:   {options.WorkingDirectory}");
Console.WriteLine();

var factory = new AgentSessionFactory(new LocalCommandLineRunnerFactory(), NullLoggerFactory.Instance);

// Carried across hops: the id of the session in the CURRENT link's store. Null on the first
// attempt (nothing to resume yet) and after a hop where the conversation could not be moved.
string? resumeSessionId = null;

// What to send to the current link. The original prompt to begin with; after a hop, a handoff turn
// instead — re-sending the prompt would duplicate a request the transferred history already holds.
var nextTurn = options.Prompt;

for (var attempt = 0; attempt < options.Chain.Count; attempt++)
{
    var link = options.Chain[attempt];
    Console.WriteLine($"── {link.Describe()} {(resumeSessionId is null ? "(new conversation)" : $"(resuming {resumeSessionId})")}");

    var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    if (link.Model is { Length: > 0 })
        config["model"] = link.Model;

    var spec = new AgentSessionSpec
    {
        Tool = link.Tool,
        WorkingDirectory = options.WorkingDirectory,
        Config = config.Count == 0 ? null : config,
        ResumeSessionId = resumeSessionId,
        EnableMcp = false,
    };

    TurnFailure? failure;
    string? agentSessionId;

    await using (var session = await factory.CreateSessionAsync(
        CreateBackend(link.Tool),
        spec,
        options: new AgentSessionOptions { InteractionMode = InteractionMode.AutoApprove },
        ct: shutdown.Token))
    {
        var turn = ConsumeTurnAsync(session, link.Describe(), shutdown.Token);
        await session.SendMessageAsync(nextTurn, shutdown.Token);
        failure = await turn;

        // --simulate exists because a demo should be runnable on demand: real rate limits do not
        // arrive when you want to show someone what happens. The failover path below is the same
        // either way.
        if (attempt == 0 && options.Simulate is { } simulated)
        {
            Console.WriteLine();
            Console.WriteLine($"[simulated] pretending this turn failed with {simulated}");
            failure = new TurnFailure(simulated, "simulated failure (--simulate)");
        }

        agentSessionId = session.AgentSessionId;
    }
    // The session is disposed before the transcript is read: the CLI flushes its transcript on
    // exit, and that file is what the next link is built from.

    if (failure is null)
    {
        Console.WriteLine();
        Console.WriteLine($"done on {link.Describe()}");
        return 0;
    }

    Console.WriteLine();
    Console.Error.WriteLine($"[{link.Describe()}] {failure.StatusLabel}"
        + (string.IsNullOrWhiteSpace(failure.Message) ? "" : $" — {failure.Message}"));

    if (!ShouldFailOver(failure.Kind))
    {
        Console.Error.WriteLine(
            $"{failure.Kind} is not something another provider would survive — stopping rather than "
            + "spending the rest of the chain on it.");
        return 1;
    }

    var next = attempt + 1;
    if (next >= options.Chain.Count)
    {
        Console.Error.WriteLine("no links left in the chain");
        return 1;
    }

    var hop = await MoveConversationAsync(
        link, options.Chain[next], agentSessionId, options.WorkingDirectory,
        failure, options.HandoffTemplate, options.SummariseOver, shutdown.Token);
    resumeSessionId = hop.SessionId;
    nextTurn = hop.Turn ?? options.Prompt;
}

return 1;

// ── failover ─────────────────────────────────────────────────────────────

// Reads the conversation out of the current CLI's store and writes it into the next one's, so the
// next CLI resumes with the history rather than starting cold. Returns the new session id, or
// null when there is nothing to carry — in which case the next link simply starts fresh.
static async Task<Hop> MoveConversationAsync(
    ChainLink from, ChainLink to, string? sessionId, string cwd,
    TurnFailure failure, string? handoffTemplate, int? summariseOver, CancellationToken ct)
{
    if (sessionId is null)
    {
        Console.WriteLine("   the CLI never reported a session id — starting the next link fresh");
        return new Hop(null, null);
    }

    // Same CLI, different model: the transcript is already in the right store, so resume it
    // untouched. This is the cheap hop and worth ordering first in a chain — nothing is converted,
    // so nothing is lost.
    if (from.Tool == to.Tool)
    {
        Console.WriteLine($"   same CLI — reusing session {sessionId}, nothing to convert");
        return new Hop(sessionId, null);
    }

    var source = StoreFor(from.Tool);
    var target = StoreFor(to.Tool);
    if (source is null || target is null)
    {
        Console.WriteLine(
            $"   no transcript store for {(source is null ? from.Tool : to.Tool)} yet — "
            + "starting the next link fresh (it will not remember the conversation)");
        return new Hop(null, null);
    }

    try
    {
        var read = await source.ReadAsync(sessionId, ct);
        if (read is null || read.Messages.Count == 0)
        {
            Console.WriteLine("   nothing transferable in the transcript — starting fresh");
            return new Hop(null, null);
        }

        // Drop the turn the agent never finished answering. Without this the target receives the
        // same request twice — once as history, once as the turn it is asked to do — and tends to
        // redo work that may already have taken effect.
        var trim = read.TrimIncompleteTail();
        var transcript = trim.Transcript;
        if (trim.DroppedRequest is not null)
            Console.WriteLine($"   trimmed the unproductive turn ({(trim.DroppedUnresolvedToolCall ? "its last step has no recorded result" : "nothing was produced")})");
        else if (trim.EndsMidTurn)
            Console.WriteLine("   the turn was cut off mid-way — keeping the work it did produce");

        if (transcript.Messages.Count == 0)
        {
            Console.WriteLine("   nothing left after trimming — starting fresh");
            return new Hop(null, null);
        }

        // Every hop re-ingests the whole transcript, so a long conversation can overflow the
        // target's context. Compressing it loses the turn-by-turn record but keeps the hop possible.
        if (summariseOver is { } limit && transcript.Messages.Count > limit)
        {
            var before = transcript.Messages.Count;
            transcript = transcript.Summarise();
            Console.WriteLine($"   summarised {before} messages into a briefing (over the {limit}-message limit)");
        }

        var newId = await target.WriteAsync(
            transcript, new TranscriptWriteOptions { Cwd = cwd, Model = to.Model }, ct);

        var tools = transcript.Messages.Count(m => m.ToolCall is not null || m.CommandExecution is not null);
        Console.WriteLine(
            $"   moved {transcript.Messages.Count} message(s), {tools} tool call(s) "
            + $"{from.Tool} -> {to.Tool} as {newId}");

        var turn = HandoffPrompt.Render(handoffTemplate, new HandoffContext
        {
            SourceTool = from.Tool,
            TargetTool = to.Tool,
            SourceSessionId = sessionId,
            SourcePath = read.SourcePath,
            Request = trim.OutstandingRequest,
            Reason = failure.StatusLabel,
            FailureKind = failure.Kind.ToString(),
            Cwd = cwd,
            HasUnresolvedToolCall = trim.EndsMidTurn,
        });
        return new Hop(newId, turn);
    }
    catch (TranscriptStoreException ex)
    {
        // A conversation we could not move is worth continuing without, but not worth hiding.
        Console.Error.WriteLine($"   could not move the conversation: {ex.Message}");
        Console.Error.WriteLine("   the next link starts fresh and will not remember it");
        return new Hop(null, null);
    }
}


// Which failures another provider might survive. Deliberately narrow: failing over on
// MaxTokens just burns the chain on a context that is too big
// everywhere, and SessionNotFound is deterministic.
static bool ShouldFailOver(TurnFailureKind kind) => kind is
    TurnFailureKind.RateLimited or
    TurnFailureKind.Overloaded or
    TurnFailureKind.ApiError or
    TurnFailureKind.Auth;

static ITranscriptStore? StoreFor(AgentToolKey tool) => tool switch
{
    AgentToolKey.ClaudeCodeCli => new ClaudeTranscriptStore(),
    AgentToolKey.CodexCli => new CodexTranscriptStore(),
    AgentToolKey.GithubCopilotCli => new CopilotTranscriptStore(),
    _ => null,      // no OpenCode store yet
};

static IAgentBackend CreateBackend(AgentToolKey tool) => tool switch
{
    AgentToolKey.ClaudeCodeCli => new ClaudeBackend(),
    AgentToolKey.CodexCli => new CodexBackend(),
    AgentToolKey.GithubCopilotCli => new CopilotBackend(),
    AgentToolKey.OpenCodeCli => new OpenCodeBackend(),
    _ => throw new ArgumentException($"No backend for {tool}"),
};

// ── one turn ─────────────────────────────────────────────────────────────

// Prints the turn as it streams and returns its failure, or null when it succeeded.
static async Task<TurnFailure?> ConsumeTurnAsync(IAgentSession session, string label, CancellationToken ct)
{
    await foreach (var evt in session.Output.WithCancellation(ct))
    {
        switch (evt)
        {
            case DeltaOutput { Payload: ContentDeltaPayload content }:
                Console.Write(content.Delta);
                break;

            case MessageOutput message:
                PrintMessage(label, message.Message);
                break;

            case ApiRetrying retry when ShouldFailOver(retry.Kind):
                // The CLI would keep retrying on its own — Claude ten times, honouring retry-after,
                // which is minutes of silence. With another provider available, waiting that out is
                // the wrong trade, so the turn is abandoned here and the chain moves on.
                Console.WriteLine();
                Console.WriteLine($"  [{label}] {retry.Kind} on attempt {retry.Attempt}"
                    + (retry.MaxAttempts is { } max ? $"/{max}" : "")
                    + (retry.RetryAfter is { } wait ? $", would wait {wait.TotalSeconds:0}s" : "")
                    + " — not waiting for it to give up");
                return new TurnFailure(retry.Kind, retry.Message);

            case ApiRetrying retry:
                Console.WriteLine();
                Console.WriteLine($"  [{label}] retrying after {retry.Kind} — letting the CLI recover");
                break;

            case TurnEnded turn:
                return turn.Failure;
        }
    }

    // The process died without ending its turn. Treated as an API error so the chain still moves
    // on: a CLI that vanished mid-turn is exactly when a second provider is useful.
    return new TurnFailure(TurnFailureKind.ApiError, "the CLI exited before the turn completed");
}

static void PrintMessage(string label, AgentMessage message)
{
    var text = message.Content;
    if (string.IsNullOrWhiteSpace(text) && message.CommandExecution is { } command)
        text = $"$ {command.Command}";
    if (string.IsNullOrWhiteSpace(text) && message.ToolCall is { } tool)
        text = $"tool: {tool.ToolName}";
    if (string.IsNullOrWhiteSpace(text))
        return;

    Console.WriteLine();
    Console.WriteLine($"[{label}] {message.Role}/{message.Type}: {text}");
}

static void PrintUsage()
{
    Console.WriteLine("""
        FailoverAgentMinimal — run a prompt against a chain of agent CLIs, moving the
        conversation to the next one when a turn fails for a reason another provider
        could survive (rate limit, overloaded/unreachable API, auth).

          --chain <list>     ordered links, comma separated, each tool[:model]
                             default: claude,codex
          --prompt <text>    the prompt to send
          --dir <path>       working directory (default: current)
          --simulate <kind>  force the FIRST turn to fail, so the failover path can be
                             demonstrated on demand: rate-limited | overloaded | api-error | auth
          --handoff <text>   what to send the next CLI after a hop. `default` explains the
                             handoff and asks the agent to verify before repeating work;
                             `minimal` is "You were interrupted. Continue the work."; any
                             other value is used literally.
          --handoff-file <p> read the handoff template from a file
          --summarise-over <n>  when a conversation is longer than n messages, compress it
                             into a briefing before handing it over, instead of moving the
                             whole transcript. Lossy — use it when the alternative is not
                             fitting in the target's context at all.
          --help

        Examples:
          # cheap hop first (same CLI, smaller model), then cross providers
          dotnet run -- --chain claude:opus,claude:sonnet,codex --prompt "explain this repo"

          # see the failover without waiting for a real rate limit
          dotnet run -- --chain claude,codex --simulate rate-limited --prompt "hello"

        Ordering matters: a same-CLI hop only changes the model and reuses the session
        untouched, while a cross-CLI hop has to convert the transcript. Put model changes
        first.

        The handoff template may use any of these placeholders; a line whose placeholder has
        no value is dropped, so a template can mention one that is not always known:
          {request} {reason} {failureKind} {sourceCli} {targetCli}
          {sourceSessionId} {sourcePath} {cwd} {unresolvedToolCall}

        Needs the CLIs in the chain installed and authenticated.
        """);
}

// ── options ──────────────────────────────────────────────────────────────

/// <summary>
/// The result of one hop: the session to resume in the next link, and the turn to send it.
/// A null turn means "send the original prompt" — nothing was carried across.
/// </summary>
internal sealed record Hop(string? SessionId, string? Turn);

internal sealed record ChainLink(AgentToolKey Tool, string? Model)
{
    public string Describe() => Model is { Length: > 0 } ? $"{Name}/{Model}" : Name;

    private string Name => Tool switch
    {
        AgentToolKey.ClaudeCodeCli => "claude",
        AgentToolKey.CodexCli => "codex",
        AgentToolKey.GithubCopilotCli => "copilot",
        AgentToolKey.OpenCodeCli => "opencode",
        _ => Tool.ToString(),
    };

    public static ChainLink Parse(string raw)
    {
        var parts = raw.Split(':', 2);
        var tool = parts[0].Trim().ToLowerInvariant() switch
        {
            "claude" or "claude-code" => AgentToolKey.ClaudeCodeCli,
            "codex" => AgentToolKey.CodexCli,
            "copilot" => AgentToolKey.GithubCopilotCli,
            "opencode" or "open-code" => AgentToolKey.OpenCodeCli,
            var other => throw new ArgumentException(
                $"Unknown tool '{other}'. Use claude, codex, copilot, or opencode."),
        };
        var model = parts.Length == 2 ? parts[1].Trim() : null;
        return new ChainLink(tool, string.IsNullOrWhiteSpace(model) ? null : model);
    }
}

internal sealed record FailoverOptions(
    IReadOnlyList<ChainLink> Chain,
    string WorkingDirectory,
    string Prompt,
    TurnFailureKind? Simulate,
    string? HandoffTemplate,
    int? SummariseOver,
    bool ShowHelp)
{
    public static FailoverOptions Parse(string[] args)
    {
        var chain = new List<ChainLink>();
        var dir = Environment.CurrentDirectory;
        var promptParts = new List<string>();
        TurnFailureKind? simulate = null;
        string? handoff = null;
        int? summariseOver = null;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    showHelp = true;
                    break;
                case "--chain" when i + 1 < args.Length:
                    chain.AddRange(args[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(ChainLink.Parse));
                    break;
                case "--dir" when i + 1 < args.Length:
                    dir = Path.GetFullPath(args[++i]);
                    break;
                case "--prompt" when i + 1 < args.Length:
                    promptParts.Add(args[++i]);
                    break;
                case "--simulate" when i + 1 < args.Length:
                    simulate = ParseKind(args[++i]);
                    break;
                case "--handoff" when i + 1 < args.Length:
                    handoff = args[++i] switch
                    {
                        "default" => null,                       // null => HandoffPrompt.DefaultTemplate
                        "minimal" => HandoffPrompt.MinimalTemplate,
                        var literal => literal,
                    };
                    break;
                case "--handoff-file" when i + 1 < args.Length:
                    handoff = File.ReadAllText(args[++i]);
                    break;
                case "--summarise-over" or "--summarize-over" when i + 1 < args.Length:
                    summariseOver = int.Parse(args[++i]);
                    break;
                default:
                    promptParts.Add(args[i]);
                    break;
            }
        }

        if (chain.Count == 0)
            chain.AddRange([new ChainLink(AgentToolKey.ClaudeCodeCli, null),
                            new ChainLink(AgentToolKey.CodexCli, null)]);

        var prompt = string.Join(' ', promptParts).Trim();
        if (string.IsNullOrWhiteSpace(prompt) && !showHelp)
            prompt = "Summarise this repository in three bullets.";

        return new FailoverOptions(chain, dir, prompt, simulate, handoff, summariseOver, showHelp);
    }

    private static TurnFailureKind ParseKind(string raw) => raw.Replace("-", "").ToLowerInvariant() switch
    {
        "ratelimited" or "ratelimit" => TurnFailureKind.RateLimited,
        "overloaded" => TurnFailureKind.Overloaded,
        "apierror" => TurnFailureKind.ApiError,
        "auth" => TurnFailureKind.Auth,
        _ => throw new ArgumentException(
            $"Unknown --simulate kind '{raw}'. Use rate-limited, overloaded, api-error, or auth."),
    };
}
