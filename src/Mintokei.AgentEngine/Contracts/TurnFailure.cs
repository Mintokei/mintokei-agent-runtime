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

    /// <summary>
    /// The stored session could not be resumed — its transcript no longer exists (the workspace holding
    /// it was reclaimed, or the CLI's own retention swept it). Deterministic: the identical resume can
    /// never succeed, so this must NOT be retried. The conversation is gone; the working tree is not.
    /// </summary>
    SessionNotFound,
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
        TurnFailureKind.SessionNotFound => "Session history unavailable",
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

        // "session limit" is Claude's wording for the one people actually hit — "You've hit your
        // session limit · resets 7:40am (UTC)". Without it the most common rate limit there is
        // fell through to Unknown, which is the difference between a "wait and retry" state and a
        // hard failure everywhere downstream that tells those apart.
        if (t.Contains("rate limit") || t.Contains("rate_limit") || t.Contains("ratelimit")
            || t.Contains("429") || t.Contains("too many requests")
            || t.Contains("quota") || t.Contains("usage limit") || t.Contains("session limit"))
            return TurnFailureKind.RateLimited;

        if (t.Contains("overloaded") || t.Contains("529"))
            return TurnFailureKind.Overloaded;

        // "not logged in" rather than the CLI's own remedy ("please run /login"): this classifier
        // is shared by every backend, and a provider's slash command does not belong in it.
        if (t.Contains("authentication") || t.Contains("unauthorized") || t.Contains("401")
            || t.Contains("403") || t.Contains("api key") || t.Contains("api-key")
            || t.Contains("billing") || t.Contains("credit balance")
            || t.Contains("not logged in") || t.Contains("not authenticated"))
            return TurnFailureKind.Auth;

        if (t.Contains("max turns") || t.Contains("max_turns") || t.Contains("maximum number of"))
            return TurnFailureKind.MaxTurns;

        if (t.Contains("context") && t.Contains("limit"))
            return TurnFailureKind.MaxTokens;

        // Last, because it is the broadest: a reachability failure is what is left once nothing
        // more specific has matched. The text counterparts of the 408/5xx statuses
        // <see cref="ClassifyFromStatus"/> already maps.
        if (t.Contains("unable to connect") || t.Contains("connection refused")
            || t.Contains("connectionrefused") || t.Contains("timed out") || t.Contains("timeout")
            || t.Contains("bad gateway") || t.Contains("service unavailable")
            || t.Contains("internal server error"))
            return TurnFailureKind.ApiError;

        return TurnFailureKind.Unknown;
    }

    /// <summary>
    /// Classifies an HTTP status. Preferred over <see cref="ClassifyFromText"/> when a backend
    /// reports one: a status is unambiguous, where provider wording is not.
    /// </summary>
    public static TurnFailureKind ClassifyFromStatus(int status) => status switch
    {
        429 => TurnFailureKind.RateLimited,
        529 => TurnFailureKind.Overloaded,
        401 or 402 or 403 => TurnFailureKind.Auth,
        408 or 500 or 502 or 503 or 504 => TurnFailureKind.ApiError,
        _ => TurnFailureKind.Unknown,
    };

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
