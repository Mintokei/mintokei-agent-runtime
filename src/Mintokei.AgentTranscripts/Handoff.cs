using System.Text;
using System.Text.RegularExpressions;

using Mintokei.AgentEngine.AgentTools;
using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentTranscripts;

/// <summary>
/// Facts about a conversation that has just been moved from one CLI to another, for
/// <see cref="HandoffPrompt.Render"/> to fill a template with.
/// </summary>
public sealed record HandoffContext
{
    /// <summary>The CLI the conversation came from.</summary>
    public AgentToolKey SourceTool { get; init; }

    /// <summary>The CLI it is continuing in.</summary>
    public AgentToolKey TargetTool { get; init; }

    /// <summary>The source CLI's session id.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>
    /// Absolute path of the transcript the conversation was read from. Worth passing on: conversion
    /// is lossy, and an agent that needs a detail which did not survive can go and read the original.
    /// </summary>
    public string? SourcePath { get; init; }

    /// <summary>The turn that did not complete, verbatim.</summary>
    public string? Request { get; init; }

    /// <summary>Human-readable failure, e.g. <c>Rate limited</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>Machine-readable failure kind, e.g. <c>RateLimited</c>.</summary>
    public string? FailureKind { get; init; }

    /// <summary>Working directory the conversation belongs to.</summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// True when the transferred history ends on a tool call whose result was never recorded — the
    /// case where the transcript cannot say whether the side effect actually landed.
    /// </summary>
    public bool HasUnresolvedToolCall { get; init; }
}

/// <summary>
/// Builds the message sent to the CLI a conversation has just been moved into.
///
/// Re-sending the original request is the obvious move and the wrong one: the transferred history
/// already contains it, so the target sees the same question twice and tends to redo work that may
/// already be done. A handoff turn instead states what happened and asks the agent to check the
/// current state first.
///
/// The wording is a template because the right message depends on the deployment — how much the
/// agent is trusted, whether side effects are reversible, whether anyone reads the transcript
/// afterwards. <see cref="DefaultTemplate"/> is a starting point, not a rule.
/// </summary>
public static class HandoffPrompt
{
    /// <summary>
    /// Explains the handoff and asks the agent to verify before repeating work. The verification
    /// instruction is the load-bearing part: without it an agent tends to redo the interrupted
    /// step, which is harmless for an idempotent edit and not harmless for a commit or an append.
    /// </summary>
    public const string DefaultTemplate = """
        [handoff] This conversation was moved here from {sourceCli} after the previous turn failed: {reason}.
        {unresolvedToolCall}
        The original transcript is at {sourcePath}.
        Working directory: {cwd}
        Outstanding request: {request}

        Check the current state of the workspace before repeating anything — earlier steps may have
        already taken effect even though the history does not record their result. Then finish the
        request, and say briefly what you found and what you changed.
        """;

    /// <summary>The short version, for deployments that would rather not spend tokens explaining.</summary>
    public const string MinimalTemplate = "You were interrupted. Continue the work.";

    /// <summary>Placeholder names understood by <see cref="Render"/>, without braces.</summary>
    public static IReadOnlyList<string> Placeholders { get; } =
    [
        "sourceCli", "targetCli", "sourceSessionId", "sourcePath",
        "request", "reason", "failureKind", "cwd", "unresolvedToolCall",
    ];

    private static readonly Regex Token = new(@"\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// Fills <paramref name="template"/> from <paramref name="context"/>.
    ///
    /// A line whose placeholder has no value is dropped entirely, rather than left as a sentence
    /// with a hole in it — so a template can mention <c>{sourcePath}</c> and still read correctly
    /// when the path is unknown. Unrecognised placeholders are left as written, so a typo shows up
    /// in the output instead of silently becoming empty.
    ///
    /// Because dropping is per line, keep a label on the SAME line as its placeholder
    /// (<c>Outstanding request: {request}</c>). A label on its own line above the placeholder
    /// survives when the value does not, leaving a heading with nothing under it.
    /// </summary>
    /// <param name="template">Null or blank uses <see cref="DefaultTemplate"/>.</param>
    /// <param name="context">Values to substitute.</param>
    public static string Render(string? template, HandoffContext context)
    {
        var text = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        var values = Values(context);
        var kept = new List<string>();

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var drop = false;
            var rendered = Token.Replace(line, m =>
            {
                var name = m.Groups["name"].Value;
                if (!values.TryGetValue(name, out var value))
                    return m.Value;                     // not ours — leave it visible
                if (string.IsNullOrWhiteSpace(value))
                {
                    drop = true;
                    return string.Empty;
                }
                return value;
            });

            if (!drop)
                kept.Add(rendered);
        }

        var result = string.Join('\n', kept).Trim();
        // A template made entirely of placeholders that turned out empty would otherwise send the
        // agent a blank turn, which reads as "the user said nothing" rather than "continue".
        return result.Length == 0 ? MinimalTemplate : result;
    }

    private static Dictionary<string, string?> Values(HandoffContext c) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sourceCli"] = Describe(c.SourceTool),
            ["targetCli"] = Describe(c.TargetTool),
            ["sourceSessionId"] = c.SourceSessionId,
            ["sourcePath"] = c.SourcePath,
            ["request"] = c.Request,
            ["reason"] = c.Reason,
            ["failureKind"] = c.FailureKind,
            ["cwd"] = c.Cwd,
            ["unresolvedToolCall"] = c.HasUnresolvedToolCall
                ? "The last recorded step has no result, so whether it took effect is unknown."
                : null,
        };

    private static string Describe(AgentToolKey tool) => tool switch
    {
        AgentToolKey.ClaudeCodeCli => "Claude Code",
        AgentToolKey.CodexCli => "Codex",
        AgentToolKey.GithubCopilotCli => "GitHub Copilot CLI",
        AgentToolKey.OpenCodeCli => "OpenCode",
        AgentToolKey.GeminiCli => "Gemini CLI",
        _ => tool.ToString(),
    };
}

/// <summary>The outcome of inspecting a transcript's tail before handing it to another CLI.</summary>
public sealed record TrimResult
{
    /// <summary>The transcript, with an unproductive trailing turn removed if there was one.</summary>
    public required StoredTranscript Transcript { get; init; }

    /// <summary>The user turn that was removed, or null if nothing was.</summary>
    public string? DroppedRequest { get; init; }

    /// <summary>Whether the removed tail held a tool call with no recorded result.</summary>
    public bool DroppedUnresolvedToolCall { get; init; }

    /// <summary>
    /// The last real user turn, whether or not it was trimmed — what the next CLI still owes an
    /// answer to. Available even when nothing was dropped, because a long multi-step turn that was
    /// cut off mid-way keeps its history (the completed steps are worth carrying) while still
    /// leaving the original request outstanding.
    /// </summary>
    public string? OutstandingRequest { get; init; }

    /// <summary>
    /// True when the transcript ends on tool activity rather than on the agent saying something —
    /// the signature of a turn that was cut off. Distinct from
    /// <see cref="DroppedUnresolvedToolCall"/>, which is only about the part that was removed.
    /// </summary>
    public bool EndsMidTurn { get; init; }
}

public static class TranscriptTrimming
{
    /// <summary>
    /// Inspects the tail of a transcript before it is handed to another CLI.
    ///
    /// Removes a trailing turn the agent produced <em>nothing</em> for, so the target does not
    /// receive the same request twice — once as history and once as the turn it is asked to do.
    /// A turn that got as far as running a tool still counts as unproductive: its half-done step is
    /// exactly what should not be replayed as settled history.
    ///
    /// A turn that produced prose along the way is KEPT even if it never finished. A five-file edit
    /// killed after the fourth file has four files' worth of work worth carrying; throwing that away
    /// would make the next CLI redo it. For that case nothing is dropped and
    /// <see cref="TrimResult.EndsMidTurn"/> reports that the turn was cut off.
    /// </summary>
    public static TrimResult TrimIncompleteTail(this StoredTranscript transcript)
    {
        var messages = transcript.Messages;

        var lastUser = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Type == MessageType.UserMessage)
            {
                lastUser = i;
                break;
            }
        }

        var outstanding = lastUser >= 0 ? messages[lastUser].Content : null;
        var last = messages.Count > 0 ? messages[^1] : null;
        var endsMidTurn = last is not null
            && !(last.Type == MessageType.AgentMessage && !string.IsNullOrWhiteSpace(last.Content));

        if (lastUser < 0)
        {
            return new TrimResult
            {
                Transcript = transcript, OutstandingRequest = null, EndsMidTurn = endsMidTurn,
            };
        }

        var tail = messages.Skip(lastUser + 1).ToList();
        var producedSomething = tail.Any(m =>
            m.Type == MessageType.AgentMessage && !string.IsNullOrWhiteSpace(m.Content));

        if (producedSomething)
        {
            return new TrimResult
            {
                Transcript = transcript, OutstandingRequest = outstanding, EndsMidTurn = endsMidTurn,
            };
        }

        var unresolved = tail.Any(m =>
            (m.ToolCall is not null || m.CommandExecution is not null)
            && m.Status != MessageStatus.Completed);

        return new TrimResult
        {
            Transcript = transcript with { Messages = messages.Take(lastUser).ToList() },
            DroppedRequest = outstanding,
            DroppedUnresolvedToolCall = unresolved,
            OutstandingRequest = outstanding,
            EndsMidTurn = true,
        };
    }
}
