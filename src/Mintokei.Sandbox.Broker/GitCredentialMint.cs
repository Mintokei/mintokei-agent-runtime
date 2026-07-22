using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mintokei.Sandbox.Broker;

/// <summary>A git credential the broker hands out for one host (git's <c>username</c> / <c>password</c> pair —
/// for GitHub-style tokens use <c>x-access-token</c> as the username and the token as the password).</summary>
public sealed record GitCredential(string Username, string Password);

/// <summary>
/// The credential-injection half of the per-session broker: a tiny HTTP endpoint the in-sandbox
/// <c>git-credential-broker</c> helper calls to fetch a credential for a host, on demand, at clone time. The
/// real secret lives HERE (broker-side, on the worker) — it is never seeded into the sandbox filesystem/env;
/// git receives it transiently via the helper and nothing is persisted. Reachable only over the session's
/// <c>--internal</c> network (the broker's port is not published), so only that sandbox can ask.
/// </summary>
public sealed class GitCredentialMint
{
    private readonly Dictionary<string, GitCredential> _creds;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<int> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GitCredentialMint(IReadOnlyDictionary<string, GitCredential> creds, ILogger? logger = null)
    {
        _creds = new Dictionary<string, GitCredential>(creds, StringComparer.OrdinalIgnoreCase);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Resolves to the bound port once <see cref="RunAsync"/> starts (for ephemeral port 0 in tests).</summary>
    public Task<int> BoundPort => _bound.Task;

    /// <summary>The configured credential for <paramref name="host"/>, or null. Pure — usable without a listener.</summary>
    public GitCredential? Resolve(string host) => _creds.TryGetValue(host.Trim(), out var c) ? c : null;

    /// <summary>Parse a <c>host=user:token</c> list (comma / whitespace / newline separated) into a host→cred map.
    /// The value splits on the FIRST colon, so tokens may contain colons.</summary>
    public static Dictionary<string, GitCredential> ParseCreds(string spec)
    {
        var map = new Dictionary<string, GitCredential>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in spec.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            var host = entry[..eq].Trim();
            var value = entry[(eq + 1)..];
            var colon = value.IndexOf(':');
            if (colon < 0) continue;
            map[host] = new GitCredential(value[..colon], value[(colon + 1)..]);
        }
        return map;
    }

    /// <summary>Listen on <paramref name="port"/> (0 = ephemeral) and serve credential lookups until cancelled.</summary>
    public async Task RunAsync(int port, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        _bound.TrySetResult(((IPEndPoint)listener.LocalEndpoint).Port);
        _logger.LogInformation("sandbox broker git-credential mint on :{Port} ({Count} host credentials)",
            ((IPEndPoint)listener.LocalEndpoint).Port, _creds.Count);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }
                _ = HandleAsync(client, ct);
            }
        }
        finally { listener.Stop(); }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var requestLine = await ReadLineAsync(stream, ct);
                string header;
                do { header = await ReadLineAsync(stream, ct); } while (header.Length > 0); // drain headers

                var host = ParseHostQuery(requestLine);
                var cred = host is null ? null : Resolve(host);
                if (cred is null)
                {
                    _logger.LogWarning("git-credential mint MISS host={Host}", host);
                    await WriteAsync(stream, "404 Not Found", "", ct);
                    return;
                }

                _logger.LogInformation("git-credential mint HIT host={Host} user={User}", host, cred.Username);
                // git's credential format — the helper relays this straight to git's stdout.
                await WriteAsync(stream, "200 OK", $"username={cred.Username}\npassword={cred.Password}\n", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "git-credential mint error");
            }
        }
    }

    // "GET /git-credential?host=example.test HTTP/1.1" → "example.test"
    private static string? ParseHostQuery(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return null;
        var i = parts[1].IndexOf("host=", StringComparison.Ordinal);
        if (i < 0) return null;
        var host = parts[1][(i + 5)..];
        var amp = host.IndexOf('&');
        if (amp >= 0) host = host[..amp];
        return host.Length == 0 ? null : Uri.UnescapeDataString(host);
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0 || one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') sb.Append((char)one[0]);
            if (sb.Length > 8192) break;
        }
        return sb.ToString();
    }

    private static async Task WriteAsync(Stream stream, string status, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status}\r\nContent-Length: {bytes.Length}\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        if (bytes.Length > 0) await stream.WriteAsync(bytes, ct);
    }
}
