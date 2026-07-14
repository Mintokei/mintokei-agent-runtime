using System.Text.Json;

namespace Mintokei.AgentEngine.Contracts;

/// <summary>A single tool-call argument as a key/value pair. Only scalar values (string, number,
/// bool) are surfaced; null/object/array values are skipped.</summary>
public sealed record ToolArgument(string Key, string Value);

/// <summary>
/// Normalizes a raw tool-arguments JSON string (a flat object like <c>{"query":"…","limit":50}</c>)
/// into a provider-agnostic list of <see cref="ToolArgument"/> pairs.
/// </summary>
public static class ToolArgumentNormalizer
{
    /// <summary>Parses <paramref name="raw"/> into scalar key/value pairs. Returns an empty list for
    /// null/empty/non-object/invalid input; never throws.</summary>
    public static IReadOnlyList<ToolArgument> Parse(string? raw)
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
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            var args = new List<ToolArgument>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null, // skip null / object / array
                };

                if (value is not null)
                    args.Add(new ToolArgument(prop.Name, value));
            }

            return args;
        }
    }
}
