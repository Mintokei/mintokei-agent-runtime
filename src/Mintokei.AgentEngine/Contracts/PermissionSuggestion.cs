using System.Text.Json;

namespace Mintokei.AgentEngine.Contracts;

/// <summary>
/// A CLI-suggested permission update the user can accept as a quick action. <see cref="Type"/>
/// discriminates the intent; type-specific fields are populated where they apply and left
/// null/empty otherwise. Real types seen: <c>addRules</c>, <c>setMode</c>, <c>addDirectories</c>
/// (Claude) and <c>mcpSessionScope</c> (Codex, bare).
/// </summary>
public sealed record PermissionSuggestion(
    string Type,
    string? Behavior,
    string? Mode,
    string? Destination,
    IReadOnlyList<SuggestionRule> Rules,
    IReadOnlyList<string> Directories);

/// <summary>A single tool rule inside an <c>addRules</c> suggestion.</summary>
public sealed record SuggestionRule(string ToolName, string? RuleContent);

/// <summary>
/// Normalizes a raw suggestions JSON string into a provider-agnostic list of
/// <see cref="PermissionSuggestion"/>, flattening the per-type shapes to a common record.
/// </summary>
public static class SuggestionNormalizer
{
    /// <summary>Parses <paramref name="raw"/> into suggestions. Returns an empty list for
    /// null/empty/invalid input; never throws.</summary>
    public static IReadOnlyList<PermissionSuggestion> Parse(string? raw)
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

            var suggestions = new List<PermissionSuggestion>();
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object)
                    continue;

                var type = GetString(s, "type");
                if (type is null)
                    continue;

                var rules = new List<SuggestionRule>();
                if (s.TryGetProperty("rules", out var r) && r.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rule in r.EnumerateArray())
                    {
                        if (rule.ValueKind != JsonValueKind.Object)
                            continue;
                        var toolName = GetString(rule, "toolName");
                        if (toolName is null)
                            continue;
                        rules.Add(new SuggestionRule(toolName, GetString(rule, "ruleContent")));
                    }
                }

                var directories = new List<string>();
                if (s.TryGetProperty("directories", out var d) && d.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dir in d.EnumerateArray())
                    {
                        if (dir.ValueKind == JsonValueKind.String && dir.GetString() is { } ds)
                            directories.Add(ds);
                    }
                }

                suggestions.Add(new PermissionSuggestion(
                    type,
                    GetString(s, "behavior"),
                    GetString(s, "mode"),
                    GetString(s, "destination"),
                    rules,
                    directories));
            }

            return suggestions;
        }
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
