using System.Security.Cryptography;

using Microsoft.Extensions.Options;

namespace Mintokei.Memory.AgentEngine;
// Dedicated config/session directory: the server inherits subscription login only,
// never the developer's MCP servers, hooks, plugins or instruction configuration.
public sealed class MemoryCodexHome(IOptions<MemoryEngineOptions> options)
{
    private readonly SemaphoreSlim gate = new(1);
    public string SourceHome => options.Value.CodexLoginHome;
    public string RuntimeHome => Path.GetFullPath(options.Value.RuntimeHome);
    public bool HasFileLogin => File.Exists(Path.Combine(SourceHome, "auth.json"));

    public async Task<string> PrepareAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Path.GetFullPath(SourceHome).TrimEnd(Path.DirectorySeparatorChar) == RuntimeHome.TrimEnd(Path.DirectorySeparatorChar))
                throw new InvalidOperationException("App Server runtime home must be separate from the Codex login home.");
            if (!HasFileLogin)
                throw new InvalidOperationException("Codex App Server needs the server's file-based Codex login. Sign in with codex -c cli_auth_credentials_store=\"file\" login. No provider fallback was used.");
            Directory.CreateDirectory(RuntimeHome);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(RuntimeHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var auth = await File.ReadAllBytesAsync(Path.Combine(SourceHome, "auth.json"), ct);
            var fingerprint = Convert.ToHexString(SHA256.HashData(auth));
            var stamp = Path.Combine(RuntimeHome, ".login-source-hash");
            // Keep tokens refreshed by the running server until the original login changes.
            if (!File.Exists(stamp) || await File.ReadAllTextAsync(stamp, ct) != fingerprint || !File.Exists(Path.Combine(RuntimeHome, "auth.json")))
            {
                await PrivateWrite("auth.json", auth, ct);
                await PrivateWrite(".login-source-hash", System.Text.Encoding.UTF8.GetBytes(fingerprint), ct);
            }

            await PrivateWrite("config.toml", "cli_auth_credentials_store = \"file\"\n"u8.ToArray(), ct);
            var models = Path.Combine(SourceHome, "models_cache.json");
            if (File.Exists(models) && !File.Exists(Path.Combine(RuntimeHome, "models_cache.json")))
                await PrivateWrite("models_cache.json", await File.ReadAllBytesAsync(models, ct), ct);
            return RuntimeHome;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PrivateWrite(string name, byte[] content, CancellationToken ct)
    {
        var temp = Path.Combine(RuntimeHome, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temp, options))
                await file.WriteAsync(content, ct);
            File.Move(temp, Path.Combine(RuntimeHome, name), true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
