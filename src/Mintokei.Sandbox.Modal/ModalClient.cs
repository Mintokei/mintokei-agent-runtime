using System.Diagnostics;
using System.Text.Json;

namespace Mintokei.Sandbox.Modal;

/// <summary>Resolved only on the coordinator. Never sent to the sandbox or included in diagnostics.</summary>
public sealed record ModalCredentials(string TokenId, string TokenSecret)
{
    public override string ToString() => "ModalCredentials { [redacted] }";
}

/// <summary>One explicitly selected account/environment. Register separate clients for separate targets;
/// no ambient Modal profile or process-global credentials are used.</summary>
public sealed record ModalClientOptions
{
    public required string AppName { get; init; }
    public required string OwnerId { get; init; }
    public string OwnerTag { get; init; } = "mintokei_owner";
    public string ResourcePrefix { get; init; } = "mintokei";
    public string Environment { get; init; } = "main";
    public string Python { get; init; } = "python3";
    public string Script { get; init; } = Path.Combine(AppContext.BaseDirectory, "sandbox", "modal_runtime.py");
    public string Bundle { get; init; } = Path.Combine(AppContext.BaseDirectory, "sandbox", "bundle");
    public int SandboxTimeoutSeconds { get; init; } = 3600;
}

/// <summary>A bound account transport. Payloads are trusted host inputs, never model-selected cloud actions.</summary>
public interface IModalClient
{
    Task<JsonElement> CallAsync(string operation, object payload, CancellationToken ct = default);
}

/// <summary>Versioned JSON bridge to the official Modal Python SDK. Credentials travel over stdin only.</summary>
public sealed class ModalClient(ModalClientOptions options,
    Func<CancellationToken, Task<ModalCredentials>> credentials) : IModalClient
{
    public async Task<JsonElement> CallAsync(string operation, object payload, CancellationToken ct = default)
    {
        var start = new ProcessStartInfo(options.Python)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(options.Script);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("MODAL_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        // An absent, unique config file prevents a coordinator's local profile changing the target or SDK mode.
        start.Environment["MODAL_CONFIG_PATH"] = Path.Combine(Path.GetTempPath(), "mintokei-modal-" + Guid.NewGuid().ToString("N"), "config.toml");
        start.Environment["MODAL_ENVIRONMENT"] = options.Environment;
        // Resolve before starting a child; a failed secret lookup must not leave a waiting process.
        var credential = await credentials(ct);
        var input = JsonSerializer.Serialize(new
        {
            version = 1, operation, appName = options.AppName, ownerId = options.OwnerId,
            ownerTag = options.OwnerTag, environment = options.Environment, credential, payload,
            resourcePrefix = options.ResourcePrefix,
            bundle = options.Bundle, timeout = options.SandboxTimeoutSeconds
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch { throw new SandboxRuntimeException("The Modal helper could not be started."); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(operation is "launch" or "native_start" ? 240 : operation == "workspace_command" ? 150 : 45));
        using var cancel = deadline.Token.Register(() => Kill(process));
        var output = ReadBounded(process.StandardOutput, deadline.Token);
        var errors = Drain(process.StandardError, deadline.Token);
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            // Read concurrently with exit: oversized stdout must fail without deadlocking on a full pipe.
            var text = await output;
            await process.WaitForExitAsync(deadline.Token);
            await errors;
            using var json = JsonDocument.Parse(text);
            if (process.ExitCode != 0 || json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.TryGetProperty("error", out _))
                throw new SandboxRuntimeException("Modal could not complete the request. Check account access and deployment configuration.");
            return json.RootElement.Clone();
        }
        catch (JsonException) { throw new SandboxRuntimeException("The Modal helper returned an invalid response."); }
        catch (IOException) { throw new SandboxRuntimeException("The Modal helper disconnected before completing the request."); }
        finally
        {
            Kill(process);
            await deadline.CancelAsync();
            // Observe both readers on every error path without returning provider diagnostics.
            try { await Task.WhenAll(output, errors); } catch { }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[65537]; var length = 0; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(length), ct)) > 0)
        {
            length += count;
            if (length > 65536) throw new SandboxRuntimeException("The Modal helper returned an oversized response.");
        }
        return new string(buffer, 0, length);
    }
    private static async Task Drain(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, ct) > 0) { }
    }
}
