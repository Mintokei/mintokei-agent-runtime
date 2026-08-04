using Mintokei.AgentEngine.Contracts;

namespace Mintokei.AgentTranscripts;

/// <summary>
/// A point where the provider, rather than the conversation, ended a turn — a rate limit, a
/// session limit, an API error.
/// </summary>
public sealed record TranscriptFailure
{
    /// <summary>Position in <see cref="StoredTranscript.Messages"/> of the failure itself.</summary>
    public required int Index { get; init; }

    /// <summary>What the CLI showed, verbatim: <c>You've hit your session limit · resets 7:40am (UTC)</c>.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// The failure classified into a kind, for a caller that would rather switch than match text.
    ///
    /// Comes from what the store read off the raw event — the provider's own <c>error</c> subtype
    /// and HTTP status — falling back to the wording only when a transcript carries neither.
    /// </summary>
    public TurnFailureKind Kind { get; init; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// Whether the conversation went on to produce work after this point.
    ///
    /// Usually true, and that is the thing worth knowing before cutting. A limit is a scar rather
    /// than an ending: the person waits for the reset, types "continue", and the session runs for
    /// hours more. Cutting at a failure that was survived throws away everything that came after
    /// it, so a caller offering to cut should say which failures these are.
    /// </summary>
    public bool Recovered { get; init; }
}

/// <summary>
/// Finds where a stored conversation was interrupted by its provider, and cuts it back to the
/// state it was in at that moment.
///
/// This is the shape of the thing the tool exists for. You move a conversation to another CLI
/// <em>because</em> one stopped answering, and what you want in the new agent is the conversation
/// as it stood when that happened — not the retries, the "continue", and the half-turn that the
/// dying session went on to record.
/// </summary>
public static class TranscriptFailures
{
    /// <summary>
    /// Every provider failure in <paramref name="transcript"/>, in order.
    ///
    /// Reads the classification the store made rather than matching the text, and the difference is
    /// not academic: a session that spends its afternoon debugging a 401 is full of messages that
    /// say "API Error" and are ordinary conversation. Only the store knows which lines the CLI
    /// itself flagged as its own failure.
    /// </summary>
    public static IReadOnlyList<TranscriptFailure> FindFailures(this StoredTranscript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var found = new List<TranscriptFailure>();
        var messages = transcript.Messages;

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Type != MessageType.Error)
                continue;

            var text = messages[i].Content ?? string.Empty;
            found.Add(new TranscriptFailure
            {
                Index = i,
                Text = text,
                // What the parser resolved from the raw event beats anything recoverable from here:
                // by this point the subtype and the status are gone and only the sentence is left.
                Kind = messages[i].FailureKind ?? TurnFailure.ClassifyFromText(text),
                At = messages[i].CreatedAt,
                Recovered = ProducedWorkAfter(messages, i),
            });
        }

        return found;
    }

    /// <summary>
    /// The transcript as it stood immediately before <paramref name="failure"/>: the failure and
    /// everything after it are dropped.
    ///
    /// The result generally ends mid-turn — the last thing recorded before a provider gives up is
    /// whatever tool call it was in the middle of — which is exactly what
    /// <see cref="TranscriptTrimming.TrimIncompleteTail"/> is for. Run that afterwards rather than
    /// duplicating it here.
    /// </summary>
    public static StoredTranscript CutBefore(this StoredTranscript transcript, TranscriptFailure failure)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(failure);

        if (failure.Index < 0 || failure.Index > transcript.Messages.Count)
            throw new ArgumentOutOfRangeException(nameof(failure), "That failure is not in this transcript.");

        return transcript with { Messages = transcript.Messages.Take(failure.Index).ToList() };
    }

    // Tool calls and their results keep being recorded while a turn is failing, so "something came
    // after" is not the question. The question is whether the agent ever spoke again.
    private static bool ProducedWorkAfter(IReadOnlyList<AgentMessage> messages, int index)
    {
        for (var i = index + 1; i < messages.Count; i++)
        {
            if (messages[i].Type == MessageType.AgentMessage
                && !string.IsNullOrWhiteSpace(messages[i].Content))
            {
                return true;
            }
        }

        return false;
    }
}
