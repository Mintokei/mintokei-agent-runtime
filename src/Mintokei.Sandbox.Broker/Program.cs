using Microsoft.Extensions.Logging;
using Mintokei.Sandbox.Broker;

// Minimal host for the per-session sandbox broker's egress proxy. Runs as the broker container (attached to the
// session's --internal network + a normal network) so it is the sandbox's only route out. Config via args/env:
//   --port <n>       (or BROKER_PORT,  default 3128)  listen port
//   --allow a,b,.c   (or BROKER_ALLOW)                comma/space-separated allowlist (exact host or ".suffix")

var port = 3128;
var allow = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var p)) port = p;
    else if (args[i] == "--allow" && i + 1 < args.Length) allow.AddRange(Split(args[++i]));
}
if (int.TryParse(Environment.GetEnvironmentVariable("BROKER_PORT"), out var envPort)) port = envPort;
allow.AddRange(Split(Environment.GetEnvironmentVariable("BROKER_ALLOW") ?? ""));

static IEnumerable<string> Split(string s) =>
    s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var proxy = new AllowlistForwardProxy(allow, loggerFactory.CreateLogger("sandbox-broker"));
await proxy.RunAsync(port, cts.Token);
