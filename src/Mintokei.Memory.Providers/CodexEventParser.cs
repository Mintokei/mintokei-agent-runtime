using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory.Providers;

public static class CodexEventParser
{
    public static ParsedCodexEvent Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Unparsed(line);
            var duplicates = new List<string>();
            var node = (JsonObject)Normalize(document.RootElement, "", duplicates)!;
            // Codex 0.164 web-search items emit both item and execution IDs as
            // "id". JsonNode defers the duplicate-key failure until traversal.
            // Match JSONB/browser last-property-wins semantics before streaming
            // or serialization; preserve the exact line in a separate app event.
            JsonObject? diagnostic = duplicates.Count == 0 ? null : new()
            {
                ["type"] = "application.cli_event_normalized",
                ["source"] = "codex_cli_adapter",
                ["policy"] = "last_property_wins",
                ["duplicatePropertyPaths"] = new JsonArray(duplicates.Select(p => (JsonNode? )JsonValue.Create(p)).ToArray()),
                ["rawLine"] = line
            };
            return new(node, diagnostic);
        }
        catch (JsonException)
        {
            return Unparsed(line);
        }
    }

    private static ParsedCodexEvent Unparsed(string line) => new(new JsonObject { ["type"] = "unparsed", ["text"] = line }, null);
    private static JsonNode? Normalize(JsonElement element, string path, List<string> duplicates)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var property in element.EnumerateObject())
            {
                var childPath = path + "/" + property.Name.Replace("~", "~0").Replace("/", "~1");
                if (result.ContainsKey(property.Name))
                    duplicates.Add(childPath);
                result[property.Name] = Normalize(property.Value, childPath, duplicates);
            }

            return result;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray();
            foreach (var item in element.EnumerateArray())
                result.Add(Normalize(item, path + "/" + result.Count, duplicates));
            return result;
        }

        return JsonNode.Parse(element.GetRawText());
    }
}
