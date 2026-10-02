using System.Diagnostics;
using System.Text.Json;

namespace Mintokei.AgentAuthentication;

public sealed record DeviceChallenge(string Url, string Code)
{
    public override string ToString() => "DeviceChallenge { [redacted] }";
}
public sealed record CodexLoginResult(string AuthJson, string AccountId, string? Identity, string[] Models)
{
    public override string ToString() => "CodexLoginResult { [redacted] }";
}
public interface ICodexAccountClient
{
    Task<CodexLoginResult> Login(Func<DeviceChallenge, Task> challenge, CancellationToken ct);
    Task<string> Refresh(string authJson, CancellationToken ct);
}

// Auth-only use of the official Codex app-server. This process never starts a
// thread or executes model/tool requests. Each operation has an isolated home.
public sealed class CodexAccountClient(CodexAccountOptions options) : ICodexAccountClient
{
    public async Task<CodexLoginResult> Login(Func<DeviceChallenge, Task> challenge, CancellationToken ct)
        => await Sanitize(() => LoginCore(challenge, ct));

    public async Task<string> Refresh(string authJson, CancellationToken ct)
        => await Sanitize(() => RefreshCore(authJson, ct));

    private static async Task<T> Sanitize<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (Exception ex) when (ex is not (AgentAuthenticationException or OperationCanceledException))
        { throw new AgentAuthenticationException("Codex authentication could not complete. Check the CLI installation or reconnect the account."); }
    }

    private async Task<CodexLoginResult> LoginCore(Func<DeviceChallenge, Task> challenge, CancellationToken ct)
    {
        await using var rpc = await AuthProcess.Start(options, null, ct);
        var start = await rpc.Call("account/login/start", new { type = "chatgptDeviceCode" }, ct);
        var url = start.GetProperty("verificationUrl").GetString()!;
        AuthenticationGuard.Require(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host is "auth.openai.com" or "chatgpt.com", "Unexpected Codex sign-in address.");
        var code = start.GetProperty("userCode").GetString()!;
        AuthenticationGuard.Require(code.Length is > 0 and <= 100, "Invalid sign-in challenge.");
        await challenge(new(url, code));
        var loginId = start.GetProperty("loginId").GetString();
        while (true)
        {
            var notification = await rpc.Read(ct);
            if (!notification.TryGetProperty("method", out var method) || method.GetString() != "account/login/completed") continue;
            var result = notification.GetProperty("params");
            if (result.GetProperty("loginId").GetString() != loginId) continue;
            AuthenticationGuard.Require(result.GetProperty("success").GetBoolean(), "Codex sign-in was not completed. Try connecting again.");
            break;
        }
        var account = (await rpc.Call("account/read", new { refreshToken = false }, ct)).GetProperty("account");
        AuthenticationGuard.Require(account.ValueKind == JsonValueKind.Object && account.GetProperty("type").GetString() == "chatgpt", "A personal Codex account is required.");
        var auth = await rpc.Auth(ct); var credentials = CodexCredential.Parse(auth);
        var models = (await rpc.Call("model/list", new { limit = 100 }, ct)).GetProperty("data").EnumerateArray()
            .OrderByDescending(m => m.TryGetProperty("isDefault", out var d) && d.GetBoolean())
            .Select(m => m.GetProperty("model").GetString()!).Where(m => m.Length <= 100).Distinct().Take(30).ToArray();
        AuthenticationGuard.Require(models.Length > 0, "No available models were returned for this account.");
        return new(auth, credentials.AccountId, account.TryGetProperty("email", out var email) ? email.GetString() : null, models);
    }
    private async Task<string> RefreshCore(string authJson, CancellationToken ct)
    {
        await using var rpc = await AuthProcess.Start(options, authJson, ct);
        await rpc.Call("account/read", new { refreshToken = true }, ct);
        return await rpc.Auth(ct);
    }

    private sealed class AuthProcess(Process process, string home) : IAsyncDisposable
    {
        private int id;
        public static async Task<AuthProcess> Start(CodexAccountOptions options, string? auth, CancellationToken ct)
        {
            var executable = options.Executable;
            AuthenticationGuard.Require(!string.IsNullOrWhiteSpace(executable), "Codex account sign-in is not enabled on this deployment.");
            var home = Path.Combine(Path.GetTempPath(), "mintokei-auth-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var process = new Process { StartInfo = new(executable!) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = home } };
            // No inherited host CLI login, API key or project configuration.
            process.StartInfo.Environment.Clear();
            process.StartInfo.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
            process.StartInfo.Environment["HOME"] = home; process.StartInfo.Environment["CODEX_HOME"] = home;
            process.StartInfo.ArgumentList.Add("app-server");
            process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            var rpc = new AuthProcess(process, home);
            try
            {
                if (auth != null) await File.WriteAllTextAsync(Path.Combine(home, "auth.json"), auth, ct);
                process.Start();
                // Discard diagnostics; third-party output can contain credentials.
                process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
                await rpc.Call("initialize", new { clientInfo = new { name = options.ClientName, version = "1.0.0" } }, ct);
                await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(), ct);
                return rpc;
            }
            catch { await rpc.DisposeAsync(); throw; }
        }
        public async Task<JsonElement> Call(string method, object args, CancellationToken ct)
        {
            var requestId = ++id;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = requestId, method, @params = args }).AsMemory(), ct);
            while (true)
            {
                var message = await Read(ct);
                if (!message.TryGetProperty("id", out var received) || !received.TryGetInt32(out var n) || n != requestId) continue;
                AuthenticationGuard.Require(!message.TryGetProperty("error", out _), "Codex authentication could not complete. Reconnect the account.");
                return message.GetProperty("result").Clone();
            }
        }
        public async Task<JsonElement> Read(CancellationToken ct)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            AuthenticationGuard.Require(line != null && line.Length < 2 * 1024 * 1024, "Codex authentication process ended unexpectedly.");
            using var json = JsonDocument.Parse(line!); return json.RootElement.Clone();
        }
        public async Task<string> Auth(CancellationToken ct)
        {
            var text = await File.ReadAllTextAsync(Path.Combine(home, "auth.json"), ct);
            AuthenticationGuard.Require(text.Length < 100_000, "Invalid Codex credential response."); return text;
        }
        public async ValueTask DisposeAsync()
        {
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } } catch (InvalidOperationException) { }
            process.Dispose();
            if (Directory.Exists(home)) Directory.Delete(home, true);
        }
    }
}

public sealed record CodexCredential(string AccessToken, string AccountId, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "CodexCredential { [redacted] }";
    public static CodexCredential Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json); var tokens = document.RootElement.GetProperty("tokens");
            var token = tokens.GetProperty("access_token").GetString(); var account = tokens.GetProperty("account_id").GetString();
            AuthenticationGuard.Require(token is { Length: > 20 and < 20000 } && !token.Any(char.IsWhiteSpace) && account is { Length: > 0 and <= 200 } && !account.Any(char.IsWhiteSpace), "Invalid account credential.");
            var part = token!.Split('.')[1].Replace('-', '+').Replace('_', '/'); part = part.PadRight((part.Length + 3) / 4 * 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(part));
            // Expiry is only a refresh hint. Upstream validates the actual credential.
            return new(token, account!, DateTimeOffset.FromUnixTimeSeconds(claims.RootElement.GetProperty("exp").GetInt64()));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or FormatException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw new AgentAuthenticationException("Reconnect this Codex account; its credential is invalid."); }
    }
}
