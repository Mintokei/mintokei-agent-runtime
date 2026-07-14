using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.AgentEngine.AgentTools.Acp;

/// <summary>
/// JSON-RPC 2.0 helpers for stdio Agent Client Protocol (ACP) implementations
/// — currently shared by GitHub Copilot CLI and OpenCode (sst). The wire format
/// is identical (initialize / session/new / session/load / session/prompt with
/// session/update notifications); only the executable name differs.
/// </summary>
public static class AcpJsonRpcHelper
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task SendRequestAsync(
        IProcessHandle handle, int id, string method, object? @params, CancellationToken ct)
    {
        var message = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params,
        }, JsonOptions);

        await handle.WriteLineAsync(message, ct);
    }

    public static async Task SendNotificationAsync(
        IProcessHandle handle, string method, object? @params, CancellationToken ct)
    {
        var message = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params,
        }, JsonOptions);

        await handle.WriteLineAsync(message, ct);
    }

    public static bool TryParseJsonRpc(string line, out JsonElement element)
    {
        element = default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("jsonrpc", out _) ||
                root.TryGetProperty("id", out _) ||
                root.TryGetProperty("method", out _))
            {
                element = root.Clone();
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    /// <summary>
    /// Extracts <c>result.sessionId</c> from a session/new or session/load response.
    /// </summary>
    public static string? ExtractSessionId(JsonElement response)
    {
        if (response.TryGetProperty("result", out var result)
            && result.TryGetProperty("sessionId", out var id))
        {
            return id.GetString();
        }

        return null;
    }

    /// <summary>
    /// Extracts <c>result.stopReason</c> from a session/prompt response.
    /// ACP values: end_turn | max_tokens | max_turn_requests | refusal | cancelled.
    /// </summary>
    public static string? ExtractStopReason(JsonElement response)
    {
        if (response.TryGetProperty("result", out var result)
            && result.TryGetProperty("stopReason", out var stop))
        {
            return stop.GetString();
        }

        return null;
    }

    /// <summary>
    /// Resolves a CLI binary on the local PATH using <c>which</c>/<c>where</c>.
    /// Throws if the binary is not found.
    /// </summary>
    public static string ResolveExecutablePath(string binaryName)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var command = isWindows ? "where" : "which";

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = command,
            Arguments = binaryName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        });

        var output = process!.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0 || string.IsNullOrEmpty(output))
            throw new InvalidOperationException(
                $"Could not find '{binaryName}' executable. Ensure it is installed and on the PATH.");

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
}
