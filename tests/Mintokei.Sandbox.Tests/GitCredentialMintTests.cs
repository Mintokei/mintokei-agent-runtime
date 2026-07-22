using System.Net;
using Mintokei.Sandbox.Broker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class GitCredentialMintTests
{
    [Fact]
    public void ParseCreds_reads_host_user_token_entries()
    {
        var map = GitCredentialMint.ParseCreds("github.com=x-access-token:ghs_abc, gitlab.com=oauth2:glpat_xyz");

        Assert.Equal(new GitCredential("x-access-token", "ghs_abc"), map["github.com"]);
        Assert.Equal(new GitCredential("oauth2", "glpat_xyz"), map["gitlab.com"]);
    }

    [Fact]
    public void ParseCreds_splits_value_on_first_colon_so_tokens_may_contain_colons()
    {
        var map = GitCredentialMint.ParseCreds("h.test=user:a:b:c");
        Assert.Equal(new GitCredential("user", "a:b:c"), map["h.test"]);
    }

    [Fact]
    public void ParseCreds_skips_malformed_entries()
    {
        // "garbage" (no '='), "no-colon.test=user" (no ':'), and "=:t" (empty host) are all dropped.
        var map = GitCredentialMint.ParseCreds("ok.test=u:t  garbage  no-colon.test=user  =:t");
        Assert.Single(map);
        Assert.Equal(new GitCredential("u", "t"), map["ok.test"]);
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        var mint = new GitCredentialMint(new Dictionary<string, GitCredential> { ["GitHub.com"] = new("u", "t") });
        Assert.Equal(new GitCredential("u", "t"), mint.Resolve("github.com"));
        Assert.Null(mint.Resolve("other.test"));
    }

    [Fact]
    public async Task Http_endpoint_returns_git_credential_format_for_known_host_and_404_otherwise()
    {
        var mint = new GitCredentialMint(new Dictionary<string, GitCredential> { ["github.com"] = new("x-access-token", "ghs_abc") });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _ = mint.RunAsync(0, cts.Token);
        var port = await mint.BoundPort;

        using var http = new HttpClient();

        var hit = await http.GetAsync($"http://127.0.0.1:{port}/git-credential?host=github.com", cts.Token);
        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        var body = await hit.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("username=x-access-token", body);
        Assert.Contains("password=ghs_abc", body);

        var miss = await http.GetAsync($"http://127.0.0.1:{port}/git-credential?host=unknown.test", cts.Token);
        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);

        await cts.CancelAsync();
    }
}
