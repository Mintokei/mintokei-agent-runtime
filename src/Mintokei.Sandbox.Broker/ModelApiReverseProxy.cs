using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mintokei.Sandbox.Broker;

/// <summary>
/// The model-API injection half of the per-session broker: a reverse proxy that terminates the sandbox's
/// <b>plaintext</b> model call (the sandbox sets e.g. <c>ANTHROPIC_BASE_URL=http://broker:PORT</c>), injects the
/// configured auth header(s), and re-originates the request over TLS to the real upstream
/// (<c>https://api.anthropic.com</c>). The API key lives HERE (broker-side, on the worker) and is never seeded
/// into the sandbox; everything else about the request/response streams through unchanged. This is the model
/// analogue of <see cref="GitCredentialMint"/> — same principle, HTTP reverse-proxy shape.
/// </summary>
public sealed class ModelApiReverseProxy : IDisposable
{
    // Never forwarded upstream (connection-scoped) — Host is re-set from the upstream URI by HttpClient.
    private static readonly HashSet<string> Hop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE",
        "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Content-Length",
    };

    private readonly Uri _upstream;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _inject;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<int> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ModelApiReverseProxy(
        string upstreamBaseUrl, IReadOnlyList<KeyValuePair<string, string>> injectHeaders,
        ILogger? logger = null, HttpMessageHandler? handler = null)
    {
        _upstream = new Uri(upstreamBaseUrl, UriKind.Absolute);
        _inject = injectHeaders;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false });
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Resolves to the listen port once <see cref="RunAsync"/> starts.</summary>
    public Task<int> BoundPort => _bound.Task;

    /// <summary>Parse header specs — one per line (or ';'-separated), each <c>Name: value</c> or <c>Name=value</c>
    /// (split on whichever of ':' / '=' comes first). E.g. <c>x-api-key=sk-ant-...</c> or
    /// <c>Authorization: Bearer sk-...</c>.</summary>
    public static List<KeyValuePair<string, string>> ParseHeaders(string spec)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var raw in spec.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = raw.IndexOf(':');
            var equals = raw.IndexOf('=');
            var cut = (colon, equals) switch
            {
                ( < 0, < 0) => -1,
                ( < 0, _) => equals,
                (_, < 0) => colon,
                _ => Math.Min(colon, equals),
            };
            if (cut <= 0) continue;
            list.Add(new KeyValuePair<string, string>(raw[..cut].Trim(), raw[(cut + 1)..].Trim()));
        }
        return list;
    }

    /// <summary>Build the upstream request for an incoming (method, pathAndQuery, headers, body): rewrite onto the
    /// upstream base, drop hop-by-hop + Host + Content-Length, split content vs. request headers, and inject the
    /// configured auth header(s) (replacing any the caller sent). Pure — unit-testable without a listener.</summary>
    public HttpRequestMessage BuildUpstreamRequest(
        string method, string pathAndQuery, IEnumerable<KeyValuePair<string, string?>> headers, Stream? body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), new Uri(_upstream, pathAndQuery.TrimStart('/')));
        if (body is not null) req.Content = new StreamContent(body);

        foreach (var (name, value) in headers)
        {
            if (value is null || Hop.Contains(name)) continue;
            if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                req.Content?.Headers.TryAddWithoutValidation(name, value);
            else
                req.Headers.TryAddWithoutValidation(name, value);
        }

        foreach (var (name, value) in _inject) // injected key wins over anything the sandbox sent
        {
            req.Headers.Remove(name);
            req.Headers.TryAddWithoutValidation(name, value);
        }
        return req;
    }

    /// <summary>Listen on <paramref name="port"/> and reverse-proxy each request to the upstream, injecting auth.</summary>
    public async Task RunAsync(int port, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Start();
        _bound.TrySetResult(port);
        _logger.LogInformation("sandbox broker model-api reverse proxy on :{Port} → {Upstream} (+{Headers} injected header(s))",
            port, _upstream, _inject.Count);
        using var reg = ct.Register(listener.Stop);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch when (ct.IsCancellationRequested) { break; }
                _ = HandleAsync(ctx, ct);
            }
        }
        catch (HttpListenerException) { /* listener stopped */ }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        try
        {
            using var upstreamReq = BuildUpstreamRequest(
                req.HttpMethod, req.RawUrl ?? "/", Enumerate(req.Headers), req.HasEntityBody ? req.InputStream : null);
            using var upResp = await _http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);

            ctx.Response.StatusCode = (int)upResp.StatusCode;
            CopyResponseHeaders(upResp, ctx.Response);
            await using var upBody = await upResp.Content.ReadAsStreamAsync(ct);
            await upBody.CopyToAsync(ctx.Response.OutputStream, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "model-api reverse proxy error");
            try { ctx.Response.StatusCode = 502; } catch { /* headers already sent */ }
        }
        finally { try { ctx.Response.Close(); } catch { /* client gone */ } }
    }

    private static IEnumerable<KeyValuePair<string, string?>> Enumerate(System.Collections.Specialized.NameValueCollection h)
    {
        foreach (string? key in h.Keys)
            if (key is not null)
                yield return new KeyValuePair<string, string?>(key, h[key]);
    }

    private static void CopyResponseHeaders(HttpResponseMessage from, HttpListenerResponse to)
    {
        foreach (var h in from.Headers.Concat(from.Content.Headers))
        {
            if (Hop.Contains(h.Key)) continue; // framing/length are managed by HttpListener
            try { to.Headers.Set(h.Key, string.Join(",", h.Value)); } catch { /* restricted header — skip */ }
        }
    }

    public void Dispose() => _http.Dispose();
}
