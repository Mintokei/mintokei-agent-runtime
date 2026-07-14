namespace Mintokei.AgentEngine.Contracts;

/// <summary>The kind of a normalized <see cref="ContentSegment"/>.</summary>
public enum ContentSegmentKind
{
    /// <summary><see cref="ContentSegment.Value"/> is plain text.</summary>
    Text,

    /// <summary><see cref="ContentSegment.Value"/> is an image URL (http(s) or data URI).</summary>
    Image,

    /// <summary><see cref="ContentSegment.Value"/> is pretty-printed JSON (a result that has no
    /// recognized text/image shape).</summary>
    Json,
}

/// <summary>
/// A normalized, provider-agnostic piece of a tool result. Every agent's raw result string —
/// MCP <c>{ content: [...] }</c>, a content-item array, plain text, or arbitrary JSON — collapses
/// to a list of these, so a consumer of the engine needn't know each provider's result shape.
/// </summary>
public sealed record ContentSegment(ContentSegmentKind Kind, string Value);
