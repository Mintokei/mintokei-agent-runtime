using System.Text.Json;

namespace Mintokei.AgentEngine;

/// <summary>
/// Shared helper for converting persisted <c>ImagesJson</c> into agent-specific input formats.
/// </summary>
public static class ImageAttachmentHelper
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Appends Codex-format image items (<c>image</c> / <c>localImage</c>) to the input list.
    /// </summary>
    public static void AppendCodexImageItems(List<object> inputItems, string? imagesJson)
    {
        if (string.IsNullOrEmpty(imagesJson)) return;

        List<ImageEntry>? images;
        try
        {
            images = JsonSerializer.Deserialize<List<ImageEntry>>(imagesJson, CamelCase);
        }
        catch
        {
            return;
        }

        if (images is null) return;

        foreach (var img in images)
        {
            if (img.Type == "base64" && !string.IsNullOrEmpty(img.Data))
            {
                // Codex expects { type: "image", url: "<data URI>" }
                inputItems.Add(new { type = "image", url = img.Data });
            }
            else if (img.Type == "localPath" && !string.IsNullOrEmpty(img.Path))
            {
                // Codex expects { type: "localImage", path: "/path/to/file" }
                inputItems.Add(new { type = "localImage", path = img.Path });
            }
        }
    }

    /// <summary>
    /// Appends ACP-format image content blocks to a Copilot <c>session/prompt</c> prompt list.
    /// Always inlines as base64 — local paths are read from disk and encoded.
    /// </summary>
    public static void AppendCopilotImageItems(List<object> prompt, string? imagesJson)
    {
        if (string.IsNullOrEmpty(imagesJson)) return;

        List<ImageEntry>? images;
        try
        {
            images = JsonSerializer.Deserialize<List<ImageEntry>>(imagesJson, CamelCase);
        }
        catch
        {
            return;
        }

        if (images is null) return;

        foreach (var img in images)
        {
            if (img.Type == "base64" && !string.IsNullOrEmpty(img.Data) && !string.IsNullOrEmpty(img.MediaType))
            {
                var base64Data = img.Data;
                var commaIndex = base64Data.IndexOf(',');
                if (commaIndex >= 0)
                    base64Data = base64Data[(commaIndex + 1)..];

                prompt.Add(new { type = "image", data = base64Data, mimeType = img.MediaType });
            }
            else if (img.Type == "localPath" && !string.IsNullOrEmpty(img.Path))
            {
                try
                {
                    var bytes = File.ReadAllBytes(img.Path);
                    var base64 = Convert.ToBase64String(bytes);
                    var ext = System.IO.Path.GetExtension(img.Path).ToLowerInvariant();
                    var mediaType = ext switch
                    {
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        ".webp" => "image/webp",
                        _ => "image/png",
                    };

                    prompt.Add(new { type = "image", data = base64, mimeType = mediaType });
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// Builds the <c>content</c> field for Claude Code stream-json messages.
    /// Returns a plain string when no images are present (backward compatible),
    /// or an array of content blocks when images exist.
    /// </summary>
    public static object BuildClaudeCodeContent(string text, string? imagesJson)
    {
        if (string.IsNullOrEmpty(imagesJson))
            return text;

        List<ImageEntry>? images;
        try
        {
            images = JsonSerializer.Deserialize<List<ImageEntry>>(imagesJson, CamelCase);
        }
        catch
        {
            return text;
        }

        if (images is null || images.Count == 0)
            return text;

        var blocks = new List<object>();

        if (!string.IsNullOrEmpty(text))
            blocks.Add(new { type = "text", text });

        foreach (var img in images)
        {
            if (img.Type == "base64" && !string.IsNullOrEmpty(img.Data) && !string.IsNullOrEmpty(img.MediaType))
            {
                // Strip data URI prefix if present (e.g. "data:image/png;base64,...")
                var base64Data = img.Data;
                var commaIndex = base64Data.IndexOf(',');
                if (commaIndex >= 0)
                    base64Data = base64Data[(commaIndex + 1)..];

                blocks.Add(new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = img.MediaType,
                        data = base64Data,
                    },
                });
            }
            else if (img.Type == "localPath" && !string.IsNullOrEmpty(img.Path))
            {
                // Read file from disk and send as base64
                try
                {
                    var bytes = File.ReadAllBytes(img.Path);
                    var base64 = Convert.ToBase64String(bytes);
                    var ext = System.IO.Path.GetExtension(img.Path).ToLowerInvariant();
                    var mediaType = ext switch
                    {
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        ".webp" => "image/webp",
                        _ => "image/png",
                    };

                    blocks.Add(new
                    {
                        type = "image",
                        source = new
                        {
                            type = "base64",
                            media_type = mediaType,
                            data = base64,
                        },
                    });
                }
                catch
                {
                    // If file can't be read, skip this image
                }
            }
        }

        return blocks.Count == 0 ? (object)text : blocks;
    }

    private sealed class ImageEntry
    {
        public string Type { get; set; } = string.Empty;
        public string? Data { get; set; }
        public string? MediaType { get; set; }
        public string? Path { get; set; }
    }
}
