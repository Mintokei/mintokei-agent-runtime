namespace Mintokei.Memory.Providers;

public sealed class OpenAiCredentials(IConfiguration configuration)
{
    public string KeyFile => Path.GetFullPath(configuration["Memory:OpenAiApiKeyFile"] ?? Path.Combine(configuration["Memory:Root"] ?? "../..", ".data", "secrets", "openai-api-key"));

    public async Task<string?> ReadAsync(CancellationToken ct = default)
    {
        var key = configuration["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(key) && File.Exists(KeyFile))
            key = await File.ReadAllTextAsync(KeyFile, ct);
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    public async Task<object> Status(CancellationToken ct = default) => new
    {
        provider = "openai_api",
        configured = await ReadAsync(ct) != null,
        configuration = "Set OPENAI_API_KEY in the API server environment, or place the key in .data/secrets/openai-api-key (server only).",
        billing = "Separate OpenAI API billing. No automatic provider fallback."
    };
}
