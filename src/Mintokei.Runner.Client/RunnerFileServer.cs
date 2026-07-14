using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mintokei.Runner;

/// <summary>
/// In-process Kestrel server bound to <c>localhost:0</c> that serves arbitrary
/// files from the runner machine's filesystem. Reachable only from the API,
/// via the existing tunnel WebSocket — never exposed to the public network.
///
/// The server itself does no path validation: callers must validate paths
/// against the workspace root before forwarding here.
///
/// Returns files with HTTP range processing enabled, so video/audio playback gets normal seek and
/// progressive-download semantics for free.
/// </summary>
public sealed class RunnerFileServer : IHostedService
{
    private readonly ILogger<RunnerFileServer> _logger;
    private static readonly FileExtensionContentTypeProvider MimeProvider = new();
    private WebApplication? _app;

    public RunnerFileServer(ILogger<RunnerFileServer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The TCP port the server is bound to on localhost. <c>0</c> until <see cref="StartAsync"/>
    /// has completed.
    /// </summary>
    public int Port { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(opt =>
        {
            // Bind to an ephemeral port on loopback only — the only consumer
            // is the runner's own tunnel client, in the same process.
            opt.Listen(IPAddress.Loopback, 0);
        });
        // Quiet by default — every range request would otherwise log a line.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();

        app.MapGet("/file", (string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "The 'path' query parameter is required." });

            if (!File.Exists(path))
                return Results.NotFound(new { error = "File not found." });

            if (!MimeProvider.TryGetContentType(path, out var contentType))
                contentType = "application/octet-stream";

            return Results.File(path, contentType, enableRangeProcessing: true);
        });

        await app.StartAsync(cancellationToken);

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not report any bound addresses.");
        var first = addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel reported no addresses after start.");
        Port = new Uri(first).Port;
        _app = app;

        _logger.LogInformation("Runner file server bound to http://127.0.0.1:{Port}", Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
            await _app.StopAsync(cancellationToken);
    }
}
