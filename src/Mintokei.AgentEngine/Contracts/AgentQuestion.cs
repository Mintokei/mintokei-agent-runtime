using System.Text.Json;

namespace Mintokei.AgentEngine.Contracts;

/// <summary>A single interactive question the agent asked the user (e.g. Claude's AskUserQuestion).</summary>
public sealed record AgentQuestion(
    string Question,
    string? Header,
    bool MultiSelect,
    IReadOnlyList<QuestionOption> Options);

/// <summary>One selectable answer for an <see cref="AgentQuestion"/>.</summary>
public sealed record QuestionOption(string Label, string? Description, string? Preview);

/// <summary>
/// Normalizes a raw questions JSON string
/// (<c>[{"question":…,"header":…,"options":[{"label":…,"description":…}],"multiSelect":…}]</c>)
/// into a provider-agnostic list of <see cref="AgentQuestion"/>.
/// </summary>
public static class QuestionNormalizer
{
    /// <summary>Parses <paramref name="raw"/> into questions. Returns an empty list for
    /// null/empty/invalid input; never throws.</summary>
    public static IReadOnlyList<AgentQuestion> Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return [];

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return [];
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var questions = new List<AgentQuestion>();
            foreach (var q in doc.RootElement.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object)
                    continue;

                var text = GetString(q, "question");
                if (text is null)
                    continue; // the question text is required

                var multiSelect = q.TryGetProperty("multiSelect", out var ms) && ms.ValueKind == JsonValueKind.True;

                var options = new List<QuestionOption>();
                if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in opts.EnumerateArray())
                    {
                        if (o.ValueKind != JsonValueKind.Object)
                            continue;
                        var label = GetString(o, "label");
                        if (label is null)
                            continue;
                        options.Add(new QuestionOption(label, GetString(o, "description"), GetString(o, "preview")));
                    }
                }

                questions.Add(new AgentQuestion(text, GetString(q, "header"), multiSelect, options));
            }

            return questions;
        }
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
