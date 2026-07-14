using System.Text.Json;

namespace Mintokei.AgentEngine.Contracts;

/// <summary>Helper for contract fields stored as a raw JSON array of strings.</summary>
public static class JsonStringArray
{
    /// <summary>Parses <paramref name="raw"/> (e.g. <c>["TaskStop","mcp__…"]</c>) into its string
    /// elements. Returns an empty list for null/empty/invalid input; never throws.</summary>
    public static IReadOnlyList<string> Parse(string? raw)
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

            var items = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { } s)
                    items.Add(s);
            }

            return items;
        }
    }
}
