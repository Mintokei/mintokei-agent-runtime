using Microsoft.Extensions.Logging.Abstractions;

using Mintokei.AgentEngine;
using Mintokei.AgentEngine.Acp;
using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Claude;
using Mintokei.AgentEngine.CommandRunner;
using Mintokei.AgentEngine.Codex;
using Mintokei.AgentEngine.Contracts;
using Mintokei.AgentEngine.Copilot;

namespace Mintokei.Hermod;

/// <summary>What a summarising run produced, or why it produced nothing.</summary>
/// <param name="Briefing">The agent's prose, or null when the run did not finish usefully.</param>
/// <param name="Failure">Why, in a sentence the user can act on. Null on success.</param>
public sealed record SummaryAttempt(string? Briefing, string? Failure)
{
    public static SummaryAttempt Failed(string why) => new(null, why);
}

/// <summary>
/// Runs one agent CLI over a stored transcript and keeps what it says about it.
///
/// The transcript is handed over as a <em>path</em>, never as text. Pasting a conversation into a
/// prompt would hit the same context limit that made summarising worth doing; giving the agent the
/// file lets it read as much or as little as it needs with its own tools.
///
/// It reads the <em>source</em> transcript rather than the converted one, because the source still
/// holds what conversion drops — opaque reasoning, tool calls the target has no form for — so the
/// briefing can carry across things the move itself cannot.
/// </summary>
internal static class AgentSummariser
{
    /// <summary>
    /// Runs <paramref name="profile"/> over a readable copy of <paramref name="transcriptPath"/> and
    /// returns what it wrote. Never throws for an agent's own failure: the caller asked for a move,
    /// and a summary it could not produce is a reason to fall back, not to abandon the move.
    ///
    /// <paramref name="buildPrompt"/> is handed the path the agent should actually read, and runs
    /// only once that copy exists — it is not the path the transcript lives at.
    /// </summary>
    public static async Task<SummaryAttempt> RunAsync(
        Profile profile,
        Func<string, string> buildPrompt,
        string transcriptPath,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        IAgentBackend backend;
        try
        {
            backend = CreateBackend(profile.ToolKey);
        }
        catch (InvalidOperationException ex)
        {
            return SummaryAttempt.Failed(ex.Message);
        }

        // Transcripts live in the CLI's own store — ~/.claude, ~/.codex, ~/.copilot — which is
        // outside the working directory, and every one of these CLIs stops to ask before reading
        // there. Denying that request is right and also fatal: the agent has nothing to read. So it
        // is given a copy inside the directory it is already trusted with, and the permission
        // question never arises.
        string readable;
        try
        {
            readable = Path.Combine(workingDirectory, $".hermod-transcript-{Guid.NewGuid():N}.jsonl");
            File.Copy(transcriptPath, readable, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SummaryAttempt.Failed(
                $"could not place a readable copy of the transcript in {workingDirectory} — {ex.Message}");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            var prompt = buildPrompt(readable);
            var factory = new AgentSessionFactory(
                new LocalCommandLineRunnerFactory(), NullLoggerFactory.Instance);

            await using var session = await factory.CreateSessionAsync(
                backend,
                new AgentSessionSpec
                {
                    Tool = backend.Tool,
                    WorkingDirectory = workingDirectory,
                    Config = profile.Config.Count == 0 ? null : profile.Config,
                    EnableMcp = false,
                },
                options: new AgentSessionOptions
                {
                    // The one file it needs is already inside the working directory, so a request
                    // reaching here is for something else — refusing needs no policy engine, only
                    // the nerve to say no.
                    InteractionMode = InteractionMode.Deny,
                },
                ct: deadline.Token);

            var collected = ReadBriefingAsync(session, deadline.Token);
            await session.SendMessageAsync(prompt, deadline.Token);
            return await collected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SummaryAttempt.Failed(
                $"the summarising agent did not finish within {timeout.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SummaryAttempt.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Whatever happened, the copy does not outlive the run — it is in someone's repository.
            try
            {
                File.Delete(readable);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"  (could not remove {readable}: {ex.Message})");
            }
        }
    }

    private static async Task<SummaryAttempt> ReadBriefingAsync(IAgentSession session, CancellationToken ct)
    {
        var said = new List<string>();

        await foreach (var evt in session.Output.WithCancellation(ct))
        {
            switch (evt)
            {
                // Only what the agent says in its own voice. Reasoning and tool traffic are how it
                // got there, and a briefing that includes them is a transcript again.
                case MessageOutput { Message: { Role: MessageRole.Assistant, Type: MessageType.AgentMessage } m }
                    when !string.IsNullOrWhiteSpace(m.Content):
                    said.Add(m.Content!.Trim());
                    break;

                case TurnEnded { Failure: { } failure }:
                    return SummaryAttempt.Failed(string.IsNullOrWhiteSpace(failure.Message)
                        ? failure.StatusLabel
                        : $"{failure.StatusLabel} — {failure.Message}");

                case TurnEnded { IsInterrupted: true }:
                    return SummaryAttempt.Failed("the summarising agent was interrupted");

                case TurnEnded:
                    return said.Count > 0
                        ? new SummaryAttempt(string.Join("\n\n", said), null)
                        : SummaryAttempt.Failed("the summarising agent finished without saying anything");
            }
        }

        return SummaryAttempt.Failed("the summarising agent's output ended before its turn did");
    }

    private static IAgentBackend CreateBackend(AgentToolKey tool) => tool switch
    {
        AgentToolKey.ClaudeCodeCli => new ClaudeBackend(),
        AgentToolKey.CodexCli => new CodexBackend(),
        AgentToolKey.GithubCopilotCli => new CopilotBackend(),
        AgentToolKey.OpenCodeCli => new OpenCodeBackend(),
        _ => throw new InvalidOperationException($"{tool} cannot be driven as a summariser"),
    };
}
