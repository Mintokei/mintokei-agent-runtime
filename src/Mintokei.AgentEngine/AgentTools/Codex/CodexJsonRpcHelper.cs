using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mintokei.AgentEngine.CommandRunner;

namespace Mintokei.AgentEngine.AgentTools.Codex;

/// <summary>
/// Shared JSON-RPC helpers for Codex app-server communication.
/// </summary>
public static class CodexJsonRpcHelper
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

    public static async Task<JsonElement> ReadResponseAsync(
        IAsyncEnumerator<CommandOutput> enumerator, int expectedId, CancellationToken ct)
    {
        while (await enumerator.MoveNextAsync())
        {
            var line = enumerator.Current;

            if (string.IsNullOrWhiteSpace(line.Line))
                continue;

            if (!TryParseJsonRpc(line.Line, out var msg))
                continue;

            if (msg.TryGetProperty("id", out var idProp) && idProp.GetInt32() == expectedId)
            {
                // Check for JSON-RPC error response before returning
                if (msg.TryGetProperty("error", out var errorProp))
                {
                    var errorMessage = errorProp.TryGetProperty("message", out var msgProp)
                        ? msgProp.GetString() ?? "Unknown error"
                        : "Unknown error";
                    var errorCode = errorProp.TryGetProperty("code", out var codeProp)
                        ? codeProp.GetInt32()
                        : 0;
                    throw new CodexJsonRpcException(
                        $"Codex JSON-RPC error (code {errorCode}): {errorMessage}",
                        errorCode, errorMessage, msg);
                }

                return msg;
            }
        }

        throw new AgentStreamEndedException($"Output stream ended before response with id {expectedId} was received.");
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
    /// Extracts <c>result.turn.id</c> from a turn/start response.
    /// Returns null if the path doesn't exist (e.g. unexpected response shape).
    /// </summary>
    public static string? ExtractTurnId(JsonElement response)
    {
        if (response.TryGetProperty("result", out var result)
            && result.TryGetProperty("turn", out var turn)
            && turn.TryGetProperty("id", out var id))
        {
            return id.GetString();
        }

        return null;
    }

    /// <summary>
    /// Extracts <c>result.thread.id</c> from a thread/start or thread/resume response.
    /// Returns null if the path doesn't exist.
    /// </summary>
    public static string? ExtractThreadId(JsonElement response)
    {
        if (response.TryGetProperty("result", out var result)
            && result.TryGetProperty("thread", out var thread)
            && thread.TryGetProperty("id", out var id))
        {
            return id.GetString();
        }

        return null;
    }

    public static string ResolveCodexExecutablePath()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var command = isWindows ? "where" : "which";

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = command,
            Arguments = "codex",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        });

        var output = process!.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0 || string.IsNullOrEmpty(output))
            throw new InvalidOperationException(
                "Could not find 'codex' executable. Ensure it is installed and on the PATH.");

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

/// <summary>
/// Thrown when a Codex JSON-RPC response contains an error instead of a result.
/// </summary>
public sealed class CodexJsonRpcException : InvalidOperationException
{
    public int ErrorCode { get; }
    public string ErrorMessage { get; }
    public JsonElement RawResponse { get; }

    public CodexJsonRpcException(string message, int errorCode, string errorMessage, JsonElement rawResponse)
        : base(message)
    {
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        RawResponse = rawResponse;
    }
}
