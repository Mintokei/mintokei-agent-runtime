using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.AgentEngine.AgentTools.Claude;

/// <summary>
/// Shared helpers for Claude Code subprocess communication.
/// </summary>
public static class ClaudeCodeHelper
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ResolveExecutablePath()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var command = isWindows ? "where" : "which";

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = command,
            Arguments = "claude",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        });

        var output = process!.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0 || string.IsNullOrEmpty(output))
            throw new InvalidOperationException(
                "Could not find 'claude' executable. Ensure it is installed and on the PATH.");

        var candidates = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        if (isWindows)
        {
            var executable = candidates.FirstOrDefault(p =>
                    p.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                ?? candidates[0];
            return executable;
        }

        return candidates[0];
    }

    public static bool TryParseJson(string line, out JsonElement element)
    {
        element = default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task SendControlRequestAsync(
        IProcessHandle handle, string requestId, object request, CancellationToken ct)
    {
        var message = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = requestId,
            request,
        }, JsonOptions);

        await handle.WriteLineAsync(message, ct);
    }

    public static string BuildSessionFilePath(string workingDirectory, string sessionId)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projectKey = workingDirectory
            .Replace(":", "-")
            .Replace("\\", "-")
            .Replace("/", "-");
        return Path.Combine(userHome, ".claude", "projects", projectKey, $"{sessionId}.jsonl");
    }

    /// <summary>
    /// Reads lines from the enumerator until a <c>control_response</c> matching
    /// <paramref name="expectedRequestId"/> is found, and returns the full response body.
    /// </summary>
    public static async Task<JsonElement> ReadControlResponseWithBodyAsync(
        IAsyncEnumerator<CommandOutput> enumerator, string expectedRequestId, CancellationToken ct)
    {
        while (await enumerator.MoveNextAsync())
        {
            var line = enumerator.Current;

            if (string.IsNullOrWhiteSpace(line.Line) || line.Type != OutputType.StdOut)
                continue;

            if (!TryParseJson(line.Line, out var root))
                continue;

            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "control_response")
                continue;

            if (root.TryGetProperty("response", out var response)
                && response.TryGetProperty("request_id", out var idProp)
                && idProp.GetString() == expectedRequestId)
            {
                return root;
            }
        }

        throw new AgentStreamEndedException(
            $"Output stream ended before control_response with request_id '{expectedRequestId}' was received.",
            expectedRequestId);
    }
}
