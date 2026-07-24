using Mintokei.Sandbox.Broker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>The broker's <c>${file:…}</c>/<c>${json:…}</c> resolver — how the nested-runner broker reads a token
/// from the runner's own creds mounted into it, so the real token never travels through the control plane.</summary>
public sealed class SecretRefTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mk-secretref").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    private string F(string file) => Path.Combine(_dir, file);

    [Fact]
    public void File_ref_expands_to_the_trimmed_file_contents()
    {
        File.WriteAllText(F("token"), "  github_pat_abc\n");
        Assert.Equal("Bearer github_pat_abc", SecretRef.Resolve($"Bearer ${{file:{F("token")}}}"));
    }

    [Fact]
    public void Json_ref_expands_to_a_nested_field_leaving_the_rest_intact()
    {
        File.WriteAllText(F(".credentials.json"), """{"claudeAiOauth":{"accessToken":"sk-ant-oat-XYZ"}}""");
        Assert.Equal(
            "Authorization: Bearer sk-ant-oat-XYZ;anthropic-beta: oauth-2025-04-20",
            SecretRef.Resolve($"Authorization: Bearer ${{json:{F(".credentials.json")}#claudeAiOauth.accessToken}};anthropic-beta: oauth-2025-04-20"));
    }

    [Fact]
    public void Missing_or_malformed_resolves_to_empty_never_throws()
    {
        Assert.Equal("Bearer ", SecretRef.Resolve($"Bearer ${{file:{F("nope")}}}"));       // missing file
        Assert.Equal("Bearer ", SecretRef.Resolve($"Bearer ${{json:{F("nope")}#a.b}}"));   // missing json file
        File.WriteAllText(F("x.json"), "not json");
        Assert.Equal("", SecretRef.Resolve($"${{json:{F("x.json")}#a}}"));                  // malformed json
    }

    [Fact]
    public void Plain_inline_values_pass_through_unchanged()
        => Assert.Equal("Authorization: Bearer sk-inline", SecretRef.Resolve("Authorization: Bearer sk-inline"));
}
