using Microsoft.Extensions.DependencyInjection;

namespace Mintokei.Runner.Host.Hosting;

/// <summary>
/// Convention-bound settings for <c>AddMintokeiRunnerHost</c>, read from the <c>RunnerHost</c> configuration
/// section (appsettings, environment variables like <c>RunnerHost__SigningKey</c>, CLI args — the standard
/// providers). Every value has a dev-friendly default, so the smallest complete backend is just
/// <c>builder.AddMintokeiRunnerHost()</c> + <c>app.MapMintokeiRunnerHost()</c>.
/// </summary>
public sealed class MintokeiRunnerHostOptions
{
    public const string Section = "RunnerHost";

    /// <summary>Base64 HMAC key for runner JWTs. Empty → a random key is generated at startup (dev only —
    /// tokens won't survive a restart). Set it in Production so issued runner tokens stay valid across restarts.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>JWT issuer the runner tokens are minted with / validated against.</summary>
    public string Issuer { get; set; } = "mintokei-api";

    /// <summary>JWT audience the runner tokens are minted with / validated against.</summary>
    public string Audience { get; set; } = "mintokei-runner";

    /// <summary>EF Core SQLite connection string for the runner-infra tables. Empty → a throwaway shared-cache
    /// in-memory database kept alive for the process lifetime (dev). Point at a file for persistence, or pass
    /// your own provider via the <c>AddMintokeiRunnerHost(configureDb)</c> overload for non-SQLite databases.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Route prefix the runner-facing enroll / token endpoints are mapped under (the runner client
    /// defaults to <c>/api</c>).</summary>
    public string ApiPrefix { get; set; } = "/api";

    /// <summary>Agent-CLI backends to register, by name (<c>Claude</c> | <c>Codex</c>). Equivalent to the
    /// fluent <c>.AddClaude()</c> / <c>.AddCodex()</c>; both dedupe, so you can use either or both.</summary>
    public List<string> AgentBackends { get; set; } = [];
}

/// <summary>Fluent handle returned by <c>AddMintokeiRunnerHost</c> for selecting agent backends
/// (<c>.AddClaude()</c> / <c>.AddCodex()</c>). Exposes <see cref="Services"/> for any further registration.</summary>
public interface IMintokeiRunnerHostBuilder
{
    IServiceCollection Services { get; }
}
