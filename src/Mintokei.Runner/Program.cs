using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mintokei.Runner;

// The `reset` verb clears saved credentials so the next start re-enrolls (use it
// to move to another server or recover from a rotated/revoked secret). Detect it
// up front and strip it from the args handed to configuration — the command-line
// provider rejects bare verbs that aren't "--key value" pairs.
var resetRequested = args.Any(a => a is "reset" or "--reset");
var hostArgs = args.Where(a => a is not ("reset" or "--reset")).ToArray();

var builder = Host.CreateApplicationBuilder(hostArgs);

// Friendly short flags mapped onto the Runner config section, e.g.
//   mintokei-runner --backend https://api --token <t> --name worker-1 --data-dir ./r1
var switchMappings = new Dictionary<string, string>
{
    ["--data-dir"] = "Runner:DataDir",
    ["--backend"] = "Runner:BackendUrl",
    ["--token"] = "Runner:EnrollmentToken",
    ["--name"] = "Runner:Name",
};

// Ensure appsettings files are found relative to the executable,
// then re-add command-line args so they take highest priority.
var basePath = AppContext.BaseDirectory;
builder.Configuration
    .AddJsonFile(Path.Combine(basePath, "appsettings.json"), optional: true, reloadOnChange: true)
    .AddJsonFile(Path.Combine(basePath, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
    .AddCommandLine(hostArgs, switchMappings);

// Resolve the per-user data directory (override with --data-dir / Runner:DataDir /
// RUNNER__DataDir). Credentials and the local outbox live here, so running several
// runners on one machine just means pointing each at its own --data-dir.
var dataDir = RunnerPaths.ResolveDataDirectory(builder.Configuration["Runner:DataDir"]);
builder.Configuration["Runner:DataDir"] = dataDir;
var credentialsPath = RunnerPaths.CredentialsPath(dataDir);

if (resetRequested)
{
    if (File.Exists(credentialsPath))
    {
        File.Delete(credentialsPath);
        Console.WriteLine($"Cleared runner credentials at {credentialsPath}. Run the runner again to re-enroll.");
    }
    else
    {
        Console.WriteLine($"No runner credentials found at {credentialsPath}.");
    }
    return;
}

// Load persisted credentials (written by EnrollmentService) as a fallback layer,
// then re-add command-line args so explicit flags still win over the saved values.
if (File.Exists(credentialsPath))
{
    builder.Configuration.AddJsonFile(credentialsPath, optional: true, reloadOnChange: true);
}
builder.Configuration.AddCommandLine(hostArgs, switchMappings);

// The whole runner (options binding, transports, outbox, enrollment, file server, tunnel) is
// registered by the embeddable Mintokei.Runner.Client library. This CLI shell owns only the
// configuration layering (above) and the `reset` verb.
builder.Services.AddMintokeiRunner(builder.Configuration);

var host = builder.Build();

// Initialise the outbox and enroll (from saved credentials or the token). Both are hard prerequisites
// that must complete before the transports connect — on failure exit cleanly with a clear message
// instead of an unhandled-exception stack trace.
try
{
    await host.Services.EnsureRunnerReadyAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Runner enrollment failed: {ex.Message}");
    Environment.Exit(1);
}

await host.RunAsync();
