using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mintokei.Runner;

/// <summary>
/// Manages the lifecycle of JWT tokens for runner authentication.
/// Exchanges the long-lived secret for 30-day JWTs, refreshing before expiry.
/// </summary>
public sealed class TokenRefreshService : IDisposable
{
    private readonly RunnerOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<TokenRefreshService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _currentToken;
    private DateTimeOffset _expiresAt;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public TokenRefreshService(
        IOptions<RunnerOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<TokenRefreshService> logger)
    {
        _options = options.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// Returns a valid JWT token, refreshing if necessary.
    /// Called by SignalR's AccessTokenProvider on each connection/reconnection.
    /// </summary>
    public async Task<string?> GetCurrentTokenAsync()
    {
        // Return cached token if still valid (with 2-minute buffer)
        if (_currentToken is not null && DateTimeOffset.UtcNow.AddMinutes(2) < _expiresAt)
        {
            return _currentToken;
        }

        await _refreshLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_currentToken is not null && DateTimeOffset.UtcNow.AddMinutes(2) < _expiresAt)
            {
                return _currentToken;
            }

            return await RefreshTokenAsync();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<string?> RefreshTokenAsync()
    {
        if (!_options.MachineId.HasValue || string.IsNullOrEmpty(_options.Secret))
        {
            _logger.LogWarning("Cannot refresh token: runner is not enrolled");
            return null;
        }

        _logger.LogDebug("Refreshing runner JWT token...");

        using var http = new HttpClient { BaseAddress = new Uri(_options.BackendUrl) };

        var request = new
        {
            machineId = _options.MachineId.Value,
            secret = _options.Secret,
        };

        var response = await http.PostAsJsonAsync("/api/auth/runner-token", request, JsonOptions);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("Token refresh failed (HTTP {StatusCode}): {Error}",
                (int)response.StatusCode, errorBody);

            // A 401/403 from the token endpoint means the secret is no longer valid
            // (machine removed or secret rotated). Clear the dead credentials and shut
            // down so the next start re-enrolls instead of looping on a useless secret.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogError(
                    "Runner credentials were rejected. Clearing local credentials and shutting down — " +
                    "restart and enroll with a new token.");
                TryClearCredentials();
                _lifetime.StopApplication();
            }
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize token response");

        _currentToken = result.AccessToken;
        _expiresAt = result.ExpiresAt;

        TokenRefreshServiceLog.TokenRefreshed(_logger, _expiresAt);

        return _currentToken;
    }

    private void TryClearCredentials()
    {
        try
        {
            var dataDir = _options.DataDir ?? RunnerPaths.ResolveDataDirectory(null);
            var path = RunnerPaths.CredentialsPath(dataDir);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear runner credentials");
        }
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
    }

    private sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt);
}

internal static partial class TokenRefreshServiceLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Token refreshed, expires at {ExpiresAt}")]
    public static partial void TokenRefreshed(ILogger logger, DateTimeOffset expiresAt);
}
