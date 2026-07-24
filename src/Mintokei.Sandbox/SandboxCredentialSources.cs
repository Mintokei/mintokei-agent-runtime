using System.Text.Json;

namespace Mintokei.Sandbox;

/// <summary>
/// Readers for the STANDARD agent-CLI credential files, so a broker-secrets provider doesn't hand-roll the
/// formats. These are facts about the CLIs (where each keeps its token and in what shape), identical for every
/// consumer — hence they live in the runtime. Each takes the directory the credential lives in and returns the
/// raw secret; pair with the convention builders on <see cref="ModelUpstreamSpec"/> / <see cref="SandboxBrokerSecrets"/>.
/// All are best-effort: a missing/unreadable/malformed file yields null / empty, never throws.
/// </summary>
public static class SandboxCredentialSources
{
    /// <summary>Anthropic subscription (Max/Pro) OAuth token from <c>&lt;dir&gt;/.credentials.json</c>
    /// (<c>.claudeAiOauth.accessToken</c>) — what Claude Code writes for a logged-in subscription.</summary>
    public static string? AnthropicOAuth(string? dir) =>
        ReadJson(dir, ".credentials.json", root =>
            root.TryGetProperty("claudeAiOauth", out var o) && o.TryGetProperty("accessToken", out var t)
                ? t.GetString() : null);

    /// <summary>OpenAI API key from Codex <c>&lt;dir&gt;/auth.json</c> (<c>OPENAI_API_KEY</c>).</summary>
    public static string? OpenAiApiKey(string? dir) =>
        ReadJson(dir, "auth.json", root =>
            root.TryGetProperty("OPENAI_API_KEY", out var k) ? k.GetString() : null);

    /// <summary>Git store lines <c>&lt;dir&gt;/.git-credentials</c> (<c>https://user:token@host</c>) reshaped to the
    /// broker mint's <c>host=user:token</c> form via <see cref="SandboxBrokerSecrets.GitCredentialLine"/>.</summary>
    public static IReadOnlyList<string> GitCredentialLines(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return [];
        var path = Path.Combine(dir, ".git-credentials");
        if (!File.Exists(path)) return [];
        var lines = new List<string>();
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) continue;
                var ui = uri.UserInfo.Split(':', 2);
                lines.Add(SandboxBrokerSecrets.GitCredentialLine(
                    uri.Host, Uri.UnescapeDataString(ui[0]), ui.Length > 1 ? Uri.UnescapeDataString(ui[1]) : ""));
            }
        }
        catch (Exception) { /* best-effort: unreadable store → no git creds */ }
        return lines;
    }

    private static string? ReadJson(string? dir, string file, Func<JsonElement, string?> pick)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return pick(doc.RootElement) is { Length: > 0 } s ? s : null;
        }
        catch (Exception) { return null; } // best-effort: malformed/unreadable → treat as absent
    }
}
