using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Mintokei.Runner.Host.Server;

/// <summary>
/// Mints short-lived runner access tokens — a JWT carrying the <c>machine_id</c> claim — from a
/// machine's already-verified identity (the secret→JWT exchange behind <c>/auth/runner-token</c>).
/// (Moved from Mintokei.Api; now reads <see cref="RunnerHostServerOptions"/> instead of the Api's
/// auth options, so the runner-host server surface is self-contained.)
/// </summary>
public sealed class RunnerTokenService
{
    private readonly RunnerHostServerOptions _options;

    public RunnerTokenService(IOptions<RunnerHostServerOptions> options)
    {
        _options = options.Value;
    }

    public (string Token, DateTimeOffset ExpiresAt) GenerateToken(Guid machineId, string machineName)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(_options.TokenLifetime);

        var key = new SymmetricSecurityKey(Convert.FromBase64String(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, machineId.ToString()),
            new Claim("machine_id", machineId.ToString()),
            new Claim("machine_name", machineName),
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
