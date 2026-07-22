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

// Optional model-API injection: one reverse-proxy per configured provider — the legacy single upstream
// (BROKER_MODEL_UPSTREAM) and/or any number of named providers (BROKER_MODEL_<NAME>_UPSTREAM), each on its own
// port. Re-originates the sandbox's plaintext model call over TLS with the key added.
var modelUpstreams = ModelUpstreamConfig.FromEnvironment();

// Optional GitHub-token mint for the Copilot CLI: inject the long-lived GitHub token on Copilot's GitHub API
// calls (it points COPILOT_DEBUG_GITHUB_API_URL here) so it NEVER enters the box — Copilot gets back only a
// short-lived Copilot token from the exchange. Same auth-injecting reverse-proxy as the model path.
var githubToken = Environment.GetEnvironmentVariable("BROKER_GITHUB_TOKEN");
var githubPort = int.TryParse(Environment.GetEnvironmentVariable("BROKER_GITHUB_PORT"), out var gp) ? gp : 3132;
var githubUpstream = Environment.GetEnvironmentVariable("BROKER_GITHUB_UPSTREAM") ?? "https://api.github.com";

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
foreach (var m in modelUpstreams)
    tasks.Add(new ModelApiReverseProxy(m.Upstream, m.Headers, log).RunAsync(m.Port, cts.Token));
if (!string.IsNullOrWhiteSpace(githubToken))
    tasks.Add(new ModelApiReverseProxy(githubUpstream, [new("Authorization", $"Bearer {githubToken}")], log, label: "github-token")
        .RunAsync(githubPort, cts.Token));
await Task.WhenAll(tasks);
