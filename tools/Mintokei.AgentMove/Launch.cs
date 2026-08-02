using Microsoft.Extensions.Logging.Abstractions;

using Mintokei.AgentEngine;
using Mintokei.AgentEngine.Acp;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.Codex;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentMove;

/// <summary>
/// Continues the moved conversation in the target CLI, here, instead of printing a command to run.
///
/// What this buys over <see cref="Attacher"/> is not the settings — <see cref="CliArgs"/> gets
/// almost all of those onto a command line too — but sight. Driving the CLI over its protocol is
/// what lets agentmove answer a permission request, notice a rate limit on the first retry, or
/// move the conversation on again. A TUI can do none of that for a program watching it.
/// </summary>
internal static class Launcher
{
    /// <summary>
    /// Resumes <paramref name="sessionId"/> in <paramref name="tool"/>, sends
    /// <paramref name="firstTurn"/>, and then keeps taking turns from the terminal.
    /// </summary>
    public static async Task<int> RunAsync(
        Profile profile,
        AgentToolKey tool,
        string cwd,
        string sessionId,
        string firstTurn,
        CancellationToken ct)
    {
        var spec = new AgentSessionSpec
        {
            Tool = tool,
            WorkingDirectory = cwd,
            // The whole profile, not just the model. AgentSessionSpec.Config is what the backend's
            // config mapper reads, so this is where permissionMode / approvalPolicy / access /
            // effort stop being decoration and become how the CLI is launched.
            Config = profile.Config.Count == 0
                ? null
                : new Dictionary<string, string?>(profile.Config, StringComparer.OrdinalIgnoreCase),
            ResumeSessionId = sessionId,
            EnableMcp = false,
        };

        var factory = new AgentSessionFactory(new LocalCommandLineRunnerFactory(), NullLoggerFactory.Instance);

        IAgentSession session;
        try
        {
            session = await factory.CreateSessionAsync(
                CreateBackend(tool),
                spec,
                // Surface, not AutoApprove. A human ran this command and is still sitting here, so
                // the CLI's own permission questions can simply be asked — which is also why
                // agentmove never has to translate one CLI's permission vocabulary into another's.
                options: new AgentSessionOptions { InteractionMode = InteractionMode.Surface },
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Could not start {Describe(tool, profile)}: {ex.Message}");
            return 1;
        }

        await using (session)
        {
            Console.WriteLine();
            Console.WriteLine($"── {Describe(tool, profile)} — resumed {Short(sessionId)}, sending the handoff turn");
            Console.WriteLine("   (blank line or /quit to leave; the session stays on disk either way)");

            // One enumerator for the session's whole life. Each turn reads from it until TurnEnded
            // and then stops pulling — re-opening it per turn would be a second enumeration of a
            // stream that only has one.
            await using var stream = session.Output.GetAsyncEnumerator(ct);

            var turn = firstTurn;
            while (true)
            {
                try
                {
                    await session.SendMessageAsync(turn, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"could not send the turn: {ex.Message}");
                    break;
                }

                var ended = await ConsumeTurnAsync(session, stream, tool, ct);
                if (!ended.ProcessAlive)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("the CLI exited without finishing the turn");
                    var stderr = session.RecentStderr;
                    if (!string.IsNullOrWhiteSpace(stderr))
                        Console.Error.WriteLine(Indent(stderr.TrimEnd()));
                    break;
                }

                if (ended.Failure is { } failure)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"  {failure.StatusLabel}"
                        + (string.IsNullOrWhiteSpace(failure.Message) ? "" : $" — {failure.Message}"));
                }

                var next = NextTurn();
                if (next is null)
                    break;
                turn = next;
            }

            // The CLI's own id wins: resuming can mint a new one (Claude forks on resume), and the
            // id to hand back is the one the conversation is actually under now.
            var final = session.AgentSessionId ?? sessionId;
            Console.WriteLine();
            Console.WriteLine($"Session {final}");
            Console.WriteLine($"Pick it up again with:  {Reporting.ResumeCommand(tool, final, profile)}");
        }

        return 0;
    }

    // ── one turn ─────────────────────────────────────────────────────────

    private readonly record struct TurnOutcome(bool ProcessAlive, TurnFailure? Failure);

    private static async Task<TurnOutcome> ConsumeTurnAsync(
        IAgentSession session,
        IAsyncEnumerator<AgentStreamOutput> stream,
        AgentToolKey tool,
        CancellationToken ct)
    {
        while (await stream.MoveNextAsync())
        {
            switch (stream.Current)
            {
                // Deltas are deliberately not printed. Every one of them arrives again as the
                // completed MessageOutput, so streaming both prints each answer twice — and the
                // delta stream also carries tool-input JSON, which is noise at a chat prompt.
                case MessageOutput message:
                    PrintMessage(message.Message);
                    break;

                // Answered here rather than auto-approved: this is the whole reason agentmove can
                // move a conversation without deciding what the next CLI may do to the machine.
                case InteractionRequested request:
                    await AnswerAsync(session, request, ct);
                    break;

                case ApiRetrying retry:
                    Console.WriteLine();
                    Console.WriteLine($"  {retry.Kind} on attempt {retry.Attempt}"
                        + (retry.MaxAttempts is { } max ? $"/{max}" : "")
                        + (retry.RetryAfter is { } wait ? $", retrying in {wait.TotalSeconds:0}s" : "")
                        + " — the CLI is handling it");
                    break;

                case TurnEnded turn:
                    return new TurnOutcome(true, turn.Failure);
            }
        }

        _ = tool;
        return new TurnOutcome(false, null);
    }

    /// <summary>
    /// Answers a surfaced permission / question request from the terminal.
    ///
    /// <c>"allow"</c> and <c>"deny"</c> are the portable pair: Claude's builder takes them as-is,
    /// Codex's maps them to accept/decline, and ACP's to allow_once/reject_once —
    /// with <c>Scope: "session"</c> selecting the always-variant on each.
    /// </summary>
    private static async Task AnswerAsync(IAgentSession session, InteractionRequested request, CancellationToken ct)
    {
        var interaction = request.Message.UserInteraction;
        var questions = interaction?.QuestionList ?? [];

        Console.WriteLine();
        if (questions.Count > 0)
        {
            foreach (var q in questions)
            {
                Console.WriteLine($"  ? {q.Question}");
                foreach (var option in q.Options)
                    Console.WriteLine($"      - {option.Label}"
                        + (string.IsNullOrWhiteSpace(option.Description) ? "" : $"  ({option.Description})"));
            }

            var answer = ReadLine("  answer");
            // Sent as free text. Claude's reply builder falls back to the message when no
            // per-question answers are supplied; Codex's requestUserInput wants a structured
            // `answers` object it cannot be given from one line of text, so it receives an empty
            // one and the agent will usually ask again in prose.
            await session.RespondAsync(
                request.RequestId,
                new UserInteractionResponse(
                    string.IsNullOrWhiteSpace(answer) ? "deny" : "allow",
                    answer,
                    null),
                ct);
            return;
        }

        var what = request.NotifyCommand
            ?? interaction?.Command
            ?? request.NotifyToolName
            ?? interaction?.ToolName
            ?? "an action";
        Console.WriteLine($"  ! {Describe(request, what)}");
        if (!string.IsNullOrWhiteSpace(interaction?.Reason))
            Console.WriteLine($"    {interaction.Reason}");

        if (Console.IsInputRedirected)
        {
            // Nobody to ask. Denying is the only answer that cannot widen what the agent may do.
            Console.WriteLine("    stdin is not a terminal — denying");
            await session.RespondAsync(
                request.RequestId,
                new UserInteractionResponse("deny", "no terminal available to approve this", null),
                ct);
            return;
        }

        while (true)
        {
            Console.Write("    allow? [y]es / [n]o / [a]lways: ");
            var reply = Console.ReadLine()?.Trim().ToLowerInvariant();
            UserInteractionResponse? decision = reply switch
            {
                "y" or "yes" => new UserInteractionResponse("allow", null, null),
                "a" or "always" => new UserInteractionResponse("allow", null, null, Scope: "session"),
                "n" or "no" or "" or null => new UserInteractionResponse("deny", "declined", null),
                _ => null,
            };
            if (decision is null)
            {
                Console.WriteLine("    y, n or a");
                continue;
            }
            await session.RespondAsync(request.RequestId, decision, ct);
            return;
        }
    }

    // ── terminal ─────────────────────────────────────────────────────────

    /// <summary>The next turn to send, or null to stop.</summary>
    private static string? NextTurn()
    {
        if (Console.IsInputRedirected)
            return null;       // one turn was the whole point of a piped run

        var line = ReadLine("\n>");
        return string.IsNullOrWhiteSpace(line) || line is "/quit" or "/exit" ? null : line;
    }

    private static string? ReadLine(string prompt)
    {
        Console.Write($"{prompt} ");
        return Console.ReadLine()?.Trim();
    }

    private static void PrintMessage(AgentMessage message)
    {
        // The agent's private thinking, and the turn's own echo of what was just typed. Neither is
        // an answer; both would push the useful output off the screen.
        if (message.Type is MessageType.Reasoning or MessageType.UserMessage)
            return;

        var text = message.Content;
        if (string.IsNullOrWhiteSpace(text) && message.CommandExecution is { } command)
            text = $"$ {command.Command}";
        if (string.IsNullOrWhiteSpace(text) && message.ToolCall is { } tool)
            text = $"{tool.ToolName}";
        if (string.IsNullOrWhiteSpace(text))
            return;

        // A tool's own output is only worth a line; the agent's summary of it follows anyway.
        if (message.Type is MessageType.ToolCall or MessageType.CommandExecution)
        {
            Console.WriteLine($"  · {First(text)}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine(text.TrimEnd());
    }

    private static string First(string text)
    {
        var line = text.ReplaceLineEndings("\n").Split('\n', 2)[0].Trim();
        return line.Length > 100 ? line[..99] + "…" : line;
    }

    private static string Describe(InteractionRequested request, string what) =>
        request.NotifyCommand is { Length: > 0 }
            ? $"wants to run: {what}"
            : $"wants to use: {what}";

    private static string Describe(AgentToolKey tool, Profile profile) =>
        profile.Model is { Length: > 0 } ? $"{profile.Tool}/{profile.Model}" : profile.Tool;

    private static string Short(string id) => id.Length > 13 ? id[..13] + "…" : id;

    private static string Indent(string text) =>
        string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => "  " + l));

    private static IAgentBackend CreateBackend(AgentToolKey tool) => tool switch
    {
        AgentToolKey.ClaudeCodeCli => new ClaudeBackend(),
        AgentToolKey.CodexCli => new CodexBackend(),
        AgentToolKey.GithubCopilotCli => new CopilotBackend(),
        AgentToolKey.OpenCodeCli => new OpenCodeBackend(),
        _ => throw new ArgumentException($"No backend for {tool}"),
    };

}
