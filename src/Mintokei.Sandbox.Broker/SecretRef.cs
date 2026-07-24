using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mintokei.Sandbox.Broker;

/// <summary>
/// Resolves <c>${file:...}</c> / <c>${json:...}</c> references embedded in a secret-bearing value against files
/// MOUNTED into the broker container. This is how the nested-runner broker gets its credentials without the real
/// token ever traversing the control plane: the API puts a REFERENCE (the file path, not the token) in the
/// injected header; the broker — running on the runner, with the runner's own creds mounted RO — reads the file
/// HERE and substitutes the token at startup. Non-reference text passes through unchanged.
/// <list type="bullet">
///   <item><c>${file:/path}</c> — the file's contents, trimmed (for a file that IS the token).</item>
///   <item><c>${json:/path#a.b.c}</c> — a nested string field of a JSON file (e.g. Claude's
///     <c>.credentials.json#claudeAiOauth.accessToken</c>).</item>
/// </list>
/// A missing / unreadable / malformed file resolves to empty — the injected header is then unauthenticated and
/// upstream returns 401 (surfaced), rather than the broker crashing.
/// </summary>
public static partial class SecretRef
{
    public static string Resolve(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${", StringComparison.Ordinal))
            return value ?? "";

        return RefRegex().Replace(value, m => m.Groups[1].Value == "file"
            ? ReadFile(m.Groups[2].Value)
            : ReadJson(m.Groups[2].Value));
    }

    private static string ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch (Exception) { return ""; } // best-effort: unreadable → unauthenticated, never a crash
    }

    // spec = "/path/to/file.json#a.b.c" → the string at that nested field, or "" if anything is missing.
    private static string ReadJson(string spec)
    {
        var hash = spec.IndexOf('#');
        if (hash < 0) return "";
        var path = spec[..hash];
        try
        {
            if (!File.Exists(path)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var el = doc.RootElement;
            foreach (var field in spec[(hash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries))
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(field, out el))
                    return "";
            return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
        }
        catch (Exception) { return ""; }
    }

    [GeneratedRegex(@"\$\{(file|json):([^}]+)\}")]
    private static partial Regex RefRegex();
}
