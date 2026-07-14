namespace Mintokei.AgentEngine.Contracts;

/// <summary>
/// Why an agent turn ended unsuccessfully. Drives the failure notification's
/// headline (e.g. "Rate limited" vs a generic "Failed") so the user knows whether
/// to wait, top up billing, shorten the task, or investigate.
/// </summary>
public enum TurnFailureKind
{
    /// <summary>The turn failed but we couldn't classify why.</summary>
    Unknown,

    /// <summary>Provider rate / usage limit hit (HTTP 429, quota exhausted).</summary>
    RateLimited,

    /// <summary>Provider temporarily overloaded (HTTP 529 / "overloaded").</summary>
    Overloaded,

    /// <summary>Authentication or billing problem (bad/expired key, no credit).</summary>
    Auth,

    /// <summary>Hit the configured max-turns / max-request budget for the turn.</summary>
    MaxTurns,

    /// <summary>Hit the model's context / output length limit.</summary>
    MaxTokens,

    /// <summary>The model refused to continue (safety / policy).</summary>
    Refusal,

    /// <summary>Some other upstream API error (server error, invalid request).</summary>
    ApiError,
}

/// <summary>
/// A normalized, backend-agnostic description of a failed agent turn. Each
/// execution service (Claude stream-json, Codex / ACP JSON-RPC) maps its own
/// protocol's error signals onto this so the rest of the pipeline — the status
/// transition and the notification — never has to understand per-CLI shapes.
/// </summary>
public sealed record TurnFailure(TurnFailureKind Kind, string? Message)
{
    /// <summary>Short, user-facing headline used as the notification status line.</summary>
    public string StatusLabel => DescribeKind(Kind);

    /// <summary>Maps a kind to the short headline shown in notifications.</summary>
    public static string DescribeKind(TurnFailureKind kind) => kind switch
    {
        TurnFailureKind.RateLimited => "Rate limited",
        TurnFailureKind.Overloaded => "Overloaded",
        TurnFailureKind.Auth => "Auth error",
        TurnFailureKind.MaxTurns => "Max turns reached",
        TurnFailureKind.MaxTokens => "Context limit reached",
        TurnFailureKind.Refusal => "Refused",
        TurnFailureKind.ApiError => "API error",
        _ => "Failed",
    };

    /// <summary>
    /// Best-effort classification of a free-text error message from any backend
    /// by substring-matching the common provider error vocabularies. Returns
    /// <see cref="TurnFailureKind.Unknown"/> when nothing matches. The input is
    /// always an error string, so matching short tokens like "429" is safe enough.
    /// </summary>
    public static TurnFailureKind ClassifyFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TurnFailureKind.Unknown;

        var t = text.ToLowerInvariant();

        if (t.Contains("rate limit") || t.Contains("rate_limit") || t.Contains("ratelimit")
            || t.Contains("429") || t.Contains("too many requests")
            || t.Contains("quota") || t.Contains("usage limit"))
            return TurnFailureKind.RateLimited;

        if (t.Contains("overloaded") || t.Contains("529"))
            return TurnFailureKind.Overloaded;

        if (t.Contains("authentication") || t.Contains("unauthorized") || t.Contains("401")
            || t.Contains("403") || t.Contains("api key") || t.Contains("api-key")
            || t.Contains("billing") || t.Contains("credit balance"))
            return TurnFailureKind.Auth;

        if (t.Contains("max turns") || t.Contains("max_turns") || t.Contains("maximum number of"))
            return TurnFailureKind.MaxTurns;

        if (t.Contains("context") && t.Contains("limit"))
            return TurnFailureKind.MaxTokens;

        return TurnFailureKind.Unknown;
    }

    /// <summary>
    /// Builds a failure from a free-text error message, classifying the kind and
    /// keeping the original text as the human-readable detail. Falls back to the
    /// supplied <paramref name="fallback"/> kind when classification finds nothing.
    /// </summary>
    public static TurnFailure FromText(string? text, TurnFailureKind fallback = TurnFailureKind.ApiError)
    {
        var kind = ClassifyFromText(text);
        if (kind == TurnFailureKind.Unknown)
            kind = fallback;
        return new TurnFailure(kind, string.IsNullOrWhiteSpace(text) ? null : text);
    }
}
