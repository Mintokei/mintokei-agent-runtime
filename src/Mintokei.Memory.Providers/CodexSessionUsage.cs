using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory.Providers;

public static class CodexSessionUsage
{
    // exec's turn.completed usage is summed across model requests/tool calls.
    // It is not the retained context size. Persistent CLI rollouts separately
    // expose the last request's usage. Read only those counters; never surface
    // rollout instructions, reasoning or other session contents.
    public record Counters(int ContextTokens, JsonObject? TotalUsage);
    public static async Task<int?> ReadContextTokensAsync(string threadId, CancellationToken ct, string? home = null) => (await ReadCountersAsync(threadId, ct, home))?.ContextTokens;
    public static JsonObject? TurnDelta(JsonNode? cumulative, JsonObject? previous)
    {
        if (cumulative is not JsonObject current || previous == null)
            return null;
        var delta = new JsonObject();
        foreach (var(field, value)in current)
        {
            if (value is not JsonValue v || !v.TryGetValue<int>(out var total) || previous[field] is not JsonValue p || !p.TryGetValue<int>(out var before))
            {
                delta[field] = null;
                continue;
            }

            if (total < before)
                return null;
            delta[field] = total - before;
        }

        return delta;
    }

    public static async Task<Counters?> ReadCountersAsync(string threadId, CancellationToken ct, string? home = null)
    {
        if (!Guid.TryParseExact(threadId, "D", out _))
            return null;
        home ??= Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var directory = Path.Combine(home, "sessions");
        if (!Directory.Exists(directory))
            return null;
        try
        {
            var path = Directory.EnumerateFiles(directory, "rollout-*-" + threadId + ".jsonl", SearchOption.AllDirectories).SingleOrDefault();
            if (path == null)
                return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(file);
            Counters? counters = null;
            while (await reader.ReadLineAsync(ct)is { } line)
            {
                // Avoid parsing unrelated large tool/document payloads.
                if (!line.Contains("\"token_count\"", StringComparison.Ordinal))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var row = doc.RootElement;
                    if (row.TryGetProperty("type", out var type) && type.GetString() == "event_msg" && row.TryGetProperty("payload", out var payload) && payload.TryGetProperty("type", out var eventType) && eventType.GetString() == "token_count" && payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object && info.TryGetProperty("last_token_usage", out var last) && last.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var count) && count > 0)
                        counters = new(count, info.TryGetProperty("total_token_usage", out var cumulative) && cumulative.ValueKind == JsonValueKind.Object ? JsonNode.Parse(cumulative.GetRawText())!.AsObject() : null);
                }
                catch (JsonException)
                { /* Partially flushed last line; counters remain unavailable or earlier observed values. */
                }
            }

            return counters;
        }
        catch (Exception e)when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
