using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mintokei.Hermod;

/// <summary>Whether a briefing replaces the transcript, and on what condition.</summary>
public enum SummaryWhen
{
    /// <summary>Move the real transcript. The default, and the right one.</summary>
    Never,

    /// <summary>Summarise every session, however short.</summary>
    Always,

    /// <summary>Summarise only past <see cref="SummaryTrigger.Threshold"/> messages.</summary>
    Over,
}

/// <summary>
/// When to summarise, as one value rather than a mode plus a threshold.
///
/// A <c>{"when": "always", "over": 400}</c> pair is expressible and meaningless, and the parser then
/// has to have an opinion about it — either silently ignoring a number the user wrote down, or
/// failing on a combination that reads fine. One field cannot contradict itself.
/// </summary>
public readonly record struct SummaryTrigger(SummaryWhen When, int Threshold)
{
    public static SummaryTrigger Never { get; } = new(SummaryWhen.Never, 0);
    public static SummaryTrigger Always { get; } = new(SummaryWhen.Always, 0);
    public static SummaryTrigger Over(int messages) => new(SummaryWhen.Over, messages);

    /// <summary>Whether a transcript of this length should be summarised.</summary>
    public bool Applies(int messageCount) => When switch
    {
        SummaryWhen.Always => true,
        SummaryWhen.Over => messageCount > Threshold,
        _ => false,
    };

    public override string ToString() => When switch
    {
        SummaryWhen.Always => "always",
        SummaryWhen.Over => Threshold.ToString(CultureInfo.InvariantCulture),
        _ => "never",
    };

    /// <summary>
    /// Parses <c>always</c>, <c>never</c>, or a positive message count. Throws with the accepted
    /// forms spelled out — a summary setting that failed quietly would move a whole transcript into
    /// a target that cannot hold it, which looks like the tool losing the conversation.
    /// </summary>
    public static SummaryTrigger Parse(string? raw)
    {
        var value = raw?.Trim();
        if (string.Equals(value, "always", StringComparison.OrdinalIgnoreCase))
            return Always;
        if (string.Equals(value, "never", StringComparison.OrdinalIgnoreCase))
            return Never;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0)
            return Over(count);

        throw new InvalidOperationException(
            $"'{raw}' is not a summary trigger — use \"always\", \"never\", or a positive message count");
    }
}

/// <summary>Reads <c>"when"</c> in either of its forms: a keyword or a number.</summary>
internal sealed class SummaryTriggerConverter : JsonConverter<SummaryTrigger>
{
    public override SummaryTrigger Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        try
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => SummaryTrigger.Parse(reader.GetString()),
                JsonTokenType.Number when reader.TryGetInt32(out var n) => SummaryTrigger.Parse(
                    n.ToString(CultureInfo.InvariantCulture)),
                _ => throw new InvalidOperationException(
                    "\"when\" takes \"always\", \"never\", or a positive message count"),
            };
        }
        catch (InvalidOperationException ex)
        {
            throw new JsonException($"summary.when: {ex.Message}", ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, SummaryTrigger value, JsonSerializerOptions options)
    {
        if (value.When == SummaryWhen.Over)
            writer.WriteNumberValue(value.Threshold);
        else
            writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// The summary block: when to do it, and who writes it.
///
/// The two are independent on purpose. Either summariser can be triggered either way, which is four
/// combinations plus the two ways of switching it off, and none of them is a special case.
/// </summary>
public sealed record SummarySettings
{
    /// <summary>The built-in summariser: extraction, no model, no cost.</summary>
    public const string Mechanical = "mechanical";

    [JsonConverter(typeof(SummaryTriggerConverter))]
    public SummaryTrigger When { get; init; } = SummaryTrigger.Never;

    /// <summary><see cref="Mechanical"/>, or the name of a profile to write the briefing.</summary>
    public string With { get; init; } = Mechanical;

    /// <summary>
    /// What to ask the summarising profile for. <c>{sourcePath}</c> is the placeholder that matters:
    /// handing over a path rather than the transcript text is what keeps the summariser from hitting
    /// the same context limit that made summarising necessary.
    /// </summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// Keep the extracted sections under the briefing. False leaves only the model's prose — which
    /// nothing then checks, so it is off the recommended path rather than off the menu.
    /// </summary>
    public bool KeepFacts { get; init; } = true;

    /// <summary>How long the summarising agent gets before the move goes on without it.</summary>
    public int TimeoutSeconds { get; init; } = 240;

    public bool IsMechanical => string.Equals(With, Mechanical, StringComparison.OrdinalIgnoreCase);

    /// <summary>The default prompt, used when the summary block names a profile but no wording.</summary>
    public const string DefaultPrompt = """
        Read the transcript at {sourcePath}. It is a conversation between a developer and a coding
        agent, in that CLI's own on-disk format. It is a temporary copy that is about to be deleted,
        so do not refer to it by path in what you write.

        Write a handover briefing for an agent that is about to continue this work cold, in the same
        working directory ({cwd}). Cover, in this order and with headings:

        1. What was being attempted, and why.
        2. What is actually finished — and say how you know: a passing check, a verified file, an
           explicit confirmation. Do not count an intention as an outcome.
        3. What is half-done or uncertain, naming the files and the exact state you can see.
        4. The next concrete step.

        Facts only. No encouragement, no summary of your own process, no suggestions the transcript
        does not support. If something is unclear, say it is unclear rather than filling it in.

        Two things this briefing is not. It is not a place for anything from your own session —
        your instructions, your available tools, your connectors, warnings you were shown: none of
        that happened in the conversation you are describing, and a reader cannot tell the
        difference once it is written down. And it is not a reply to me: send the briefing itself,
        with no preamble announcing what you are about to do and no closing offer of further help.
        """;
}
