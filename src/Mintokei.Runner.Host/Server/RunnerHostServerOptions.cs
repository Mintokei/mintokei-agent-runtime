namespace Mintokei.Runner.Host.Server;

/// <summary>
/// Host-supplied configuration for the runner-facing server surface (Mintokei.Runner.Host's ASP.NET
/// pieces): the JWT signing/issuance parameters <see cref="RunnerTokenService"/> uses to mint runner
/// access tokens — and, as the extraction proceeds, the same key/issuer/audience the JWT bearer scheme
/// will validate against. Passed in via <c>AddRunnerHostServer(...)</c>.
/// </summary>
public sealed class RunnerHostServerOptions
{
    /// <summary>Base64-encoded HMAC-SHA256 signing key, shared between token issuance and validation.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>JWT issuer stamped on minted runner tokens.</summary>
    public string Issuer { get; set; } = "mintokei-api";

    /// <summary>JWT audience stamped on minted runner tokens.</summary>
    public string Audience { get; set; } = "mintokei-runner";

    /// <summary>Lifetime of a minted runner access token (default 30 days — the previous hard-coded value).</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromDays(30);
}
