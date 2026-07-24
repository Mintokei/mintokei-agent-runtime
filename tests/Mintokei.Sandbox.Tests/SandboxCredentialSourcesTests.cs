using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>The reusable readers for the standard agent-CLI credential files — locking the on-disk formats
/// (Anthropic <c>.credentials.json</c>, Codex <c>auth.json</c>, git <c>.git-credentials</c>) and the best-effort
/// (never-throw) behaviour on missing/malformed input.</summary>
public sealed class SandboxCredentialSourcesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mk-cred-test").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    private string Write(string file, string content) { var p = Path.Combine(_dir, file); File.WriteAllText(p, content); return p; }

    [Fact]
    public void AnthropicOAuth_reads_the_subscription_access_token()
    {
        Write(".credentials.json", """{"claudeAiOauth":{"accessToken":"sk-ant-oat-XYZ","refreshToken":"r"}}""");
        Assert.Equal("sk-ant-oat-XYZ", SandboxCredentialSources.AnthropicOAuth(_dir));
    }

    [Fact]
    public void AnthropicOAuth_is_null_when_absent_malformed_or_no_dir()
    {
        Assert.Null(SandboxCredentialSources.AnthropicOAuth(_dir));   // file absent
        Assert.Null(SandboxCredentialSources.AnthropicOAuth(null));   // no dir
        Write(".credentials.json", "not json");
        Assert.Null(SandboxCredentialSources.AnthropicOAuth(_dir));   // malformed → treated as absent, not thrown
    }

    [Fact]
    public void OpenAiApiKey_reads_the_key_from_codex_auth_json()
    {
        Write("auth.json", """{"OPENAI_API_KEY":"sk-openai-123"}""");
        Assert.Equal("sk-openai-123", SandboxCredentialSources.OpenAiApiKey(_dir));
    }

    [Fact]
    public void GitCredentialLines_reshapes_store_lines_to_host_equals_user_colon_token()
    {
        Write(".git-credentials", "https://x-access-token:ght_abc@github.com\nhttps://user:pw@gitlab.com\n");
        Assert.Equal(
            ["github.com=x-access-token:ght_abc", "gitlab.com=user:pw"],
            SandboxCredentialSources.GitCredentialLines(_dir));
    }

    [Fact]
    public void GitCredentialLines_is_empty_when_absent() =>
        Assert.Empty(SandboxCredentialSources.GitCredentialLines(_dir));
}
