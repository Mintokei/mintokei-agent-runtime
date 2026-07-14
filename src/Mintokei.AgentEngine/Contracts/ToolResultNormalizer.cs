using System.Text.Json;

namespace Mintokei.AgentEngine.Contracts;

/// <summary>
/// Normalizes a raw tool-result string into a provider-agnostic list of <see cref="ContentSegment"/>.
/// This is the single source of truth for interpreting the shapes different agents emit:
/// <list type="bullet">
///   <item>MCP <c>CallToolResult</c>: <c>{ "content": [ { "type":"text", "text":... } ], ... }</c></item>
///   <item>content-item arrays (Codex dynamic / Anthropic): <c>[ { "type":"text"|"inputText", ... } ]</c></item>
///   <item>plain text (not JSON)</item>
///   <item>anything else JSON → pretty-printed as a single <see cref="ContentSegmentKind.Json"/> segment</item>
/// </list>
/// </summary>
public static class ToolResultNormalizer
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>Parses <paramref name="raw"/> into normalized segments. Returns an empty list for
    /// null/empty input; never throws.</summary>
    public static IReadOnlyList<ContentSegment> Parse(string? raw)
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
            // Not JSON → it's plain text output.
            return [new ContentSegment(ContentSegmentKind.Text, raw)];
        }

        using (doc)
        {
            var root = doc.RootElement;

            // MCP CallToolResult: { content: [ ... ] }
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                var segments = ExtractContentArray(content);
                if (segments.Count > 0)
                    return segments;
            }

            // Array of content items — only when the first element looks like one (has a "type").
            // A plain array of arbitrary objects (e.g. [{ "id":..., "content":... }]) falls through
            // to the JSON pretty-print below, matching the client behaviour.
            if (root.ValueKind == JsonValueKind.Array && FirstElementHasType(root))
            {
                var segments = ExtractContentArray(root);
                if (segments.Count > 0)
                    return segments;
            }

            // Arbitrary JSON → pretty-printed.
            return [new ContentSegment(ContentSegmentKind.Json, JsonSerializer.Serialize(root, Pretty))];
        }
    }

    /// <summary>Extracts text/image segments from an array of content items.</summary>
    public static List<ContentSegment> ExtractContentArray(JsonElement items)
    {
        var segments = new List<ContentSegment>();

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            // text variants: "text", "inputText"
            if ((type is "text" or "inputText")
                && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                segments.Add(new ContentSegment(ContentSegmentKind.Text, text.GetString()!));
            }
            // image variant: "inputImage" → imageUrl
            else if (type is "inputImage"
                && item.TryGetProperty("imageUrl", out var imageUrl) && imageUrl.ValueKind == JsonValueKind.String)
            {
                segments.Add(new ContentSegment(ContentSegmentKind.Image, imageUrl.GetString()!));
            }
            // image variant: "image" → source.url
            else if (type is "image"
                && item.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object
                && source.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                segments.Add(new ContentSegment(ContentSegmentKind.Image, url.GetString()!));
            }
            // resource with embedded text
            else if (type is "resource"
                && item.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object
                && resource.TryGetProperty("text", out var rtext) && rtext.ValueKind == JsonValueKind.String)
            {
                segments.Add(new ContentSegment(ContentSegmentKind.Text, rtext.GetString()!));
            }
        }

        return segments;
    }

    private static bool FirstElementHasType(JsonElement array)
    {
        foreach (var element in array.EnumerateArray())
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("type", out _);
        return false; // empty array
    }
}
