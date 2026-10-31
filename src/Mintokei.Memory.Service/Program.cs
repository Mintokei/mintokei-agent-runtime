using Microsoft.EntityFrameworkCore;
using Mintokei.Memory;
using Mintokei.Memory.Hosting;
using Mintokei.Memory.Postgres;
using Mintokei.Memory.Providers;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("Memory") ?? throw new InvalidOperationException("ConnectionStrings:Memory is required.");
if (string.IsNullOrWhiteSpace(builder.Configuration["MemoryService:Token"]))
    throw new InvalidOperationException("MemoryService:Token must be configured before starting the memory service.");
var root = Path.GetFullPath(builder.Configuration["Memory:Root"] ?? Path.Combine(AppContext.BaseDirectory, "data"));
builder.Configuration["Memory:Root"] = root;
builder.Services.AddPostgresMemory(connection);
builder.Services.AddMemoryProviders();
builder.Services.AddMemoryProcessing();
var initialConfiguration = new MemoryConfiguration { CapturePayloads = false };
builder.Configuration.GetSection("Memory:Configuration").Bind(initialConfiguration);
var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>().CreateDbContextAsync())
    await DatabaseSetup.InitializeAsync(db, root, initialConfiguration);
app.MapSharedMemory();
app.MapGet("/health", async (IDbContextFactory<MemoryDbContext> factory, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    return await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
});
await app.RunAsync();
