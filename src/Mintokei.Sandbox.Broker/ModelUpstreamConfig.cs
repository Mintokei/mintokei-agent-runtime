namespace Mintokei.Sandbox.Broker;

/// <summary>
/// Parses the broker's model-injection config from environment variables. Supports the legacy single upstream
/// (<c>BROKER_MODEL_UPSTREAM</c> / <c>BROKER_MODEL_PORT</c> / <c>BROKER_MODEL_AUTH</c>) and any number of named
/// providers (<c>BROKER_MODEL_&lt;NAME&gt;_UPSTREAM</c> / <c>_PORT</c> / <c>_AUTH</c>). The name is an opaque
/// label here — the sandbox side maps it to a base-URL env var; each entry just becomes its own reverse-proxy
/// on its own port, which is how one broker serves several providers at once.
/// </summary>
public static class ModelUpstreamConfig
{
    /// <param name="Name">Opaque provider label (for logging).</param>
    /// <param name="Port">The reverse-proxy listen port.</param>
    /// <param name="Upstream">The real upstream base URL.</param>
    /// <param name="Headers">Auth header(s) to inject.</param>
    public sealed record Entry(string Name, int Port, string Upstream, IReadOnlyList<KeyValuePair<string, string>> Headers);

    private const string Prefix = "BROKER_MODEL_";
    private const string UpstreamSuffix = "_UPSTREAM";

    /// <summary>Parse entries from <paramref name="env"/> (keys as the OS provides them).</summary>
    public static IReadOnlyList<Entry> Parse(IReadOnlyDictionary<string, string?> env)
    {
        var entries = new List<Entry>();

        // Legacy single upstream: BROKER_MODEL_UPSTREAM (+ _PORT default 3130, + _AUTH).
        if (Value(env, "BROKER_MODEL_UPSTREAM") is { Length: > 0 } legacy)
            entries.Add(new Entry("default", PortOr(env, "BROKER_MODEL_PORT", 3130), legacy,
                ModelApiReverseProxy.ParseHeaders(Value(env, "BROKER_MODEL_AUTH") ?? "")));

        // Named providers: BROKER_MODEL_<NAME>_UPSTREAM (+ _PORT, + _AUTH). NAME is the middle segment.
        foreach (var (key, value) in env)
        {
            if (value is not { Length: > 0 }) continue;
            if (!key.StartsWith(Prefix, StringComparison.Ordinal) || !key.EndsWith(UpstreamSuffix, StringComparison.Ordinal)) continue;
            if (key.Length <= Prefix.Length + UpstreamSuffix.Length) continue;   // this IS the legacy BROKER_MODEL_UPSTREAM
            var name = key[Prefix.Length..^UpstreamSuffix.Length];
            var port = PortOr(env, $"{Prefix}{name}_PORT", 0);
            if (port <= 0) continue;                                             // a named provider must carry a port (the runtime always sets it)
            entries.Add(new Entry(name.ToLowerInvariant(), port, value,
                ModelApiReverseProxy.ParseHeaders(Value(env, $"{Prefix}{name}_AUTH") ?? "")));
        }
        return entries;
    }

    /// <summary>Convenience over the process environment.</summary>
    public static IReadOnlyList<Entry> FromEnvironment()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = e.Value as string;
        return Parse(env);
    }

    private static string? Value(IReadOnlyDictionary<string, string?> env, string key) =>
        env.TryGetValue(key, out var v) ? v : null;

    private static int PortOr(IReadOnlyDictionary<string, string?> env, string key, int fallback) =>
        int.TryParse(Value(env, key), out var p) ? p : fallback;
}
