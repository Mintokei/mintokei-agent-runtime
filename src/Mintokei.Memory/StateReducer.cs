using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public static class StateReducer
{
    public static JsonObject Apply(JsonObject previous, IEnumerable<StateChange> changes, ISet<Guid> allowedIds)
    {
        var state = (JsonObject)previous.DeepClone();
        foreach (var c in changes)
        {
            if (c.SourceMessageIds.Count == 0 || c.SourceMessageIds.Any(x => !allowedIds.Contains(x)))
                throw new InvalidOperationException("State change requires valid source IDs");
            var parts = c.Path.Split('.');
            if (parts.Length > 8 || parts.Any(x => !Regex.IsMatch(x, @"^[a-zA-Z][a-zA-Z0-9_-]{0,63}$")))
                throw new InvalidOperationException("Invalid state path");
            var target = state;
            foreach (var part in parts[..^1])
            {
                if (target[part] is null)
                    target[part] = new JsonObject();
                target = target[part] as JsonObject ?? throw new InvalidOperationException("State path crosses a scalar");
            }

            var key = parts[^1];
            var value = c.Value?.DeepClone();
            // Schema uses strings for arbitrary values. Decode only valid JSON, otherwise retain literal strings.
            if (value is JsonValue v && v.TryGetValue<string>(out var text))
            {
                try
                {
                    value = JsonNode.Parse(text);
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }

            switch (c.Operation)
            {
                case "set":
                case "supersede":
                    target[key] = value;
                    break;
                case "add":
                    if (target[key] is null)
                        target[key] = new JsonArray();
                    if (target[key] is not JsonArray arr)
                        throw new InvalidOperationException("add requires an array");
                    if (!arr.Any(x => JsonNode.DeepEquals(x, value)))
                        arr.Add(value);
                    break;
                case "remove":
                case "clear":
                    target.Remove(key);
                    break;
                case "resolve":
                    target[key] = new JsonObject
                    {
                        ["status"] = "resolved",
                        ["value"] = value
                    };
                    break;
                default:
                    throw new InvalidOperationException("Unsupported state operation");
            }
        }

        if (state.ToJsonString().Length > 32000)
            throw new InvalidOperationException("State exceeds 8k approximate tokens; consolidate before retrying.");
        return state;
    }
}
