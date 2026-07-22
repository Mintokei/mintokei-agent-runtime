using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// A tiny in-process fake Kubernetes API server: records the (method, path) of every request and returns a
/// canned metadata-only object. The typed client makes REAL HTTP calls to it (the client's own tests mock it
/// the same way — a handler-level fake trips an ArgumentNull in its pipeline), so the broker's create/delete
/// flow is asserted without a cluster.
/// </summary>
internal sealed class FakeKubeApi : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<(string Method, string Path)> _calls = [];

    public string Host { get; }

    public FakeKubeApi()
    {
        Host = $"http://127.0.0.1:{FreePort()}";
        _listener.Prefixes.Add($"{Host}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public k8s.IKubernetes Client() => new k8s.Kubernetes(new k8s.KubernetesClientConfiguration { Host = Host });

    public IReadOnlyList<(string Method, string Path)> Calls
    {
        get { lock (_calls) return _calls.ToList(); }
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            lock (_calls) _calls.Add((ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath));
            var body = Encoding.UTF8.GetBytes("{\"metadata\":{\"name\":\"x\",\"uid\":\"u\"}}");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            try { ctx.Response.OutputStream.Write(body); ctx.Response.Close(); } catch { /* client gone */ }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* already stopped */ }
    }
}
