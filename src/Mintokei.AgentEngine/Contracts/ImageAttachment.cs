using System.Text.Json;

namespace Mintokei.AgentEngine.Contracts;

/// <summary>An image attached to a message. <see cref="Data"/> is a directly-renderable source —
/// typically a <c>data:</c> URI, or an http(s) URL.</summary>
public sealed record ImageAttachment(string Data);

/// <summary>
/// Normalizes a raw images JSON string (an array like <c>[{"type":"base64","data":"data:image/png;base64,…"}]</c>)
/// into a provider-agnostic list of <see cref="ImageAttachment"/>.
/// </summary>
public static class ImageNormalizer
{
    /// <summary>Parses <paramref name="raw"/> into image attachments, reading each element's
    /// <c>data</c> (or <c>url</c>) string. Returns an empty list for null/empty/invalid input;
    /// never throws.</summary>
    public static IReadOnlyList<ImageAttachment> Parse(string? raw)
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

            var images = new List<ImageAttachment>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var src = (item.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null)
                    ?? (item.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null);

                if (!string.IsNullOrEmpty(src))
                    images.Add(new ImageAttachment(src));
            }

            return images;
        }
    }
}
