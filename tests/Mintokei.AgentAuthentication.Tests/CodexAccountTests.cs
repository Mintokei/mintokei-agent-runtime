using System.Text;
using System.Text.Json;
using Xunit;

namespace Mintokei.AgentAuthentication.Tests;

public sealed class CodexAccountTests
{
    [Fact]
    public async Task Official_login_contract_uses_an_isolated_home_and_cleans_it_after_completion()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX executable fixture, matching the deployed host.
        using var fixture = new CliFixture();
        DeviceChallenge? challenge = null;
        var result = await fixture.Client.Login(c => { challenge = c; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("fixture-code", challenge!.Code);
        Assert.Equal("account-one", result.AccountId);
        Assert.Equal(new[] { "fixture-model" }, result.Models);
        Assert.False(Directory.Exists(File.ReadAllText(fixture.HomeMarker)));
        Assert.DoesNotContain(fixture.AccessToken, result.ToString());
        Assert.DoesNotContain("fixture-code", challenge.ToString());
        Assert.DoesNotContain(fixture.AccessToken, CodexCredential.Parse(result.AuthJson).ToString());
    }

    [Fact]
    public async Task Refresh_delegates_to_CLI_and_returns_the_updated_auth_document()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new CliFixture();
        var auth = await fixture.Client.Refresh("old-auth", CancellationToken.None);
        Assert.Equal("account-one", CodexCredential.Parse(auth).AccountId);
        Assert.False(Directory.Exists(File.ReadAllText(fixture.HomeMarker)));
    }

    [Theory]
    [InlineData("{\"tokens\":{\"access_token\":null,\"account_id\":null}}")]
    [InlineData("fixture-secret")]
    [InlineData("{\"tokens\":{\"access_token\":\"not-a-jwt-but-long-enough\",\"account_id\":\"account\"}}")]
    public void Malformed_credentials_return_a_sanitized_error(string input)
    {
        var error = Assert.Throws<AgentAuthenticationException>(() => CodexCredential.Parse(input));
        Assert.DoesNotContain("fixture-secret", error.ToString());
    }

    private sealed class CliFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "codex-auth-test-" + Guid.NewGuid().ToString("N"));
        public string HomeMarker => Path.Combine(directory, "home.txt");
        public string AccessToken { get; } = "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":2000000000}")).TrimEnd('=') + ".fixture-signature";
        public CodexAccountClient Client { get; }
        public CliFixture()
        {
            Directory.CreateDirectory(directory);
            var executable = Path.Combine(directory, "codex-fixture");
            var auth = JsonSerializer.Serialize(new { tokens = new { access_token = AccessToken, account_id = "account-one" } });
            var script = """
                #!/usr/bin/env python3
                import json, os, pathlib, sys
                home = pathlib.Path(os.environ['CODEX_HOME'])
                assert str(home) == os.environ['HOME'] == os.getcwd()
                assert home.stat().st_mode & 0o777 == 0o700
                assert 'ANTHROPIC_API_KEY' not in os.environ and 'OPENAI_API_KEY' not in os.environ
                pathlib.Path(MARKER).write_text(str(home))
                assert sys.argv[1:] == ['app-server', '-c', 'cli_auth_credentials_store="file"']
                auth = AUTH
                for line in sys.stdin:
                    r = json.loads(line)
                    if 'id' not in r: continue
                    method = r['method']
                    if method == 'initialize': result = {}
                    elif method == 'account/login/start':
                        assert r['params']['type'] == 'chatgptDeviceCode'
                        (home / 'auth.json').write_text(auth)
                        result = {'verificationUrl':'https://auth.openai.com/device', 'userCode':'fixture-code', 'loginId':'login-one'}
                    elif method == 'account/read':
                        if r['params']['refreshToken']:
                            assert (home / 'auth.json').read_text() == 'old-auth'
                            (home / 'auth.json').write_text(auth)
                        result = {'account': {'type':'chatgpt', 'email':'fixture@example.test'}}
                    elif method == 'model/list': result = {'data':[{'model':'fixture-model', 'isDefault':True}]}
                    else: raise ValueError('Unexpected auth RPC')
                    print(json.dumps({'id':r['id'], 'result':result}), flush=True)
                    if method == 'account/login/start':
                        print(json.dumps({'method':'account/login/completed', 'params':{'loginId':'login-one','success':True}}), flush=True)
                """;
            File.WriteAllText(executable, script.Replace("MARKER", JsonSerializer.Serialize(HomeMarker)).Replace("AUTH", JsonSerializer.Serialize(auth)));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Client = new(new(executable));
        }
        public void Dispose() => Directory.Delete(directory, true);
    }
}
