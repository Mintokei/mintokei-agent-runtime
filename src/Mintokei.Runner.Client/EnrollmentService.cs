using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mintokei.Runner;

public sealed class EnrollmentService
{
    private readonly RunnerOptions _options;
    private readonly ILogger<EnrollmentService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public EnrollmentService(
        IOptions<RunnerOptions> options,
        ILogger<EnrollmentService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// If the runner hasn't been enrolled yet (no MachineId/Secret) but has an enrollment token,
    /// enroll with the backend and persist the credentials.
    /// </summary>
    public async Task EnsureEnrolledAsync(CancellationToken ct = default)
    {
        // Already enrolled
        if (_options.MachineId.HasValue && !string.IsNullOrEmpty(_options.Secret))
        {
            _logger.LogInformation("Runner already enrolled as machine {MachineId}", _options.MachineId);
            return;
        }

        if (string.IsNullOrEmpty(_options.EnrollmentToken) && !TryPromptForEnrollment())
        {
            throw new InvalidOperationException(
                "Runner is not enrolled and no enrollment token is configured. " +
                "Run interactively to enter one, pass --token <token> (and --backend <url>), " +
                "or set Runner:EnrollmentToken. Generate a token from the Mintokei UI.");
        }

        _logger.LogInformation("Enrolling runner with backend at {Url}...", _options.BackendUrl);

        using var http = new HttpClient { BaseAddress = new Uri(_options.BackendUrl) };

        var request = new
        {
            enrollmentToken = _options.EnrollmentToken,
            name = string.IsNullOrWhiteSpace(_options.Name) ? Environment.MachineName : _options.Name,
            osInfo = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            runnerVersion = typeof(EnrollmentService).Assembly.GetName().Version?.ToString(),
        };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("/api/machines/enroll", request, JsonOptions, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Could not reach the backend at {_options.BackendUrl}: {ex.Message}. " +
                "Check the URL and that the server is reachable.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Enrollment failed (HTTP {(int)response.StatusCode}): {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Failed to deserialize enrollment response");

        _options.MachineId = result.MachineId;
        _options.Secret = result.Secret;
        _options.EnrollmentToken = null;

        // Persist to credentials file
        await PersistCredentialsAsync(result.MachineId, result.Secret);

        _logger.LogInformation("Enrollment successful. MachineId={MachineId}", result.MachineId);
    }

    private async Task PersistCredentialsAsync(Guid machineId, string secret)
    {
        var dataDir = _options.DataDir ?? RunnerPaths.ResolveDataDirectory(null);
        var credentialsPath = RunnerPaths.CredentialsPath(dataDir);

        // Nested under "Runner" so the file maps straight onto config on the next start.
        var credentials = new
        {
            Runner = new
            {
                BackendUrl = _options.BackendUrl,
                MachineId = machineId,
                Secret = secret,
            },
        };
        var json = JsonSerializer.Serialize(credentials, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(credentialsPath, json);
        TrySetOwnerOnlyPermissions(credentialsPath);
    }

    /// <summary>
    /// Prompts for the backend URL and enrollment token when attached to an
    /// interactive console. Returns false in non-interactive contexts (services,
    /// Tauri-spawned processes) so the caller can fall back to flags/env or fail clearly.
    /// </summary>
    private bool TryPromptForEnrollment()
    {
        if (Console.IsInputRedirected || !Environment.UserInteractive)
            return false;

        Console.WriteLine("This runner is not enrolled yet.");
        Console.Write($"Backend URL [{_options.BackendUrl}]: ");
        var url = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(url))
            _options.BackendUrl = url.Trim();

        Console.Write("Enrollment token (from Mintokei UI → Runners → Add): ");
        var token = ReadSecret();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("No token entered.");
            return false;
        }

        _options.EnrollmentToken = token.Trim();
        return true;
    }

    private static string ReadSecret()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                    sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                sb.Append(key.KeyChar);
        }
        return sb.ToString();
    }

    private static void TrySetOwnerOnlyPermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort — the credentials are still usable if chmod fails.
        }
    }

    private sealed record EnrollmentResponse(Guid MachineId, string Secret);
}
