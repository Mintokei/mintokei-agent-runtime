using Microsoft.Extensions.Logging;
using Mintokei.Sandbox.Broker;

// Minimal host for the per-session sandbox broker. Runs the two halves — the egress proxy and the
// git-credential mint — attached to the session's --internal network + a normal network, so it is the
// sandbox's only route out and the only place its credentials live. Config via args/env:
//   --port <n>       (or BROKER_PORT,      default 3128)  CONNECT egress-proxy port
//   --allow a,b,.c   (or BROKER_ALLOW)                    egress allowlist (exact host or ".suffix")
//   (or BROKER_MINT_PORT, default 3129)                   git-credential mint port
//   (or BROKER_GIT_CREDS)                                 "host=user:token, host2=user2:token2" (never seeded in-box)

var port = 3128;
var allow = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var p)) port = p;
    else if (args[i] == "--allow" && i + 1 < args.Length) allow.AddRange(Split(args[++i]));
}
if (int.TryParse(Environment.GetEnvironmentVariable("BROKER_PORT"), out var envPort)) port = envPort;
allow.AddRange(Split(Environment.GetEnvironmentVariable("BROKER_ALLOW") ?? ""));

var mintPort = int.TryParse(Environment.GetEnvironmentVariable("BROKER_MINT_PORT"), out var mp) ? mp : 3129;
var creds = GitCredentialMint.ParseCreds(Environment.GetEnvironmentVariable("BROKER_GIT_CREDS") ?? "");

// Optional model-API injection: re-originate the sandbox's plaintext model call over TLS with the key added.
var modelUpstream = Environment.GetEnvironmentVariable("BROKER_MODEL_UPSTREAM"); // e.g. https://api.anthropic.com
var modelPort = int.TryParse(Environment.GetEnvironmentVariable("BROKER_MODEL_PORT"), out var xp) ? xp : 3130;
var modelHeaders = ModelApiReverseProxy.ParseHeaders(Environment.GetEnvironmentVariable("BROKER_MODEL_AUTH") ?? "");

static IEnumerable<string> Split(string s) =>
    s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var log = loggerFactory.CreateLogger("sandbox-broker");
var proxy = new AllowlistForwardProxy(allow, log);
var mint = new GitCredentialMint(creds, log);

var tasks = new List<Task>
{
    proxy.RunAsync(port, cts.Token),
    mint.RunAsync(mintPort, cts.Token),
};
if (!string.IsNullOrWhiteSpace(modelUpstream))
{
    var model = new ModelApiReverseProxy(modelUpstream, modelHeaders, log);
    tasks.Add(model.RunAsync(modelPort, cts.Token));
}
await Task.WhenAll(tasks);
