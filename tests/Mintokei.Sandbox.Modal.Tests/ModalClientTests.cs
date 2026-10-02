using Xunit;

namespace Mintokei.Sandbox.Modal.Tests;

public sealed class ModalClientTests
{
    [Fact]
    public async Task Credentials_use_stdin_and_never_command_arguments_or_ambient_profile()
    {
        using var helper = new Helper("""
            import json, os, sys
            r = json.load(sys.stdin)
            assert r['version'] == 1 and r['credential']['tokenSecret'] == 'fixture-secret'
            assert 'fixture-secret' not in ' '.join(sys.argv)
            assert 'MODAL_TOKEN_SECRET' not in os.environ
            assert os.environ['MODAL_ENVIRONMENT'] == 'testing'
            assert not os.path.exists(os.environ['MODAL_CONFIG_PATH'])
            print(json.dumps({'state': 'running'}))
            """);
        Assert.Equal("running", (await helper.Client.CallAsync("status", new { })).GetProperty("state").GetString());
        Assert.DoesNotContain("fixture-secret", new ModalCredentials("fixture-id", "fixture-secret").ToString());
    }

    [Theory]
    [InlineData("import sys; print('fixture-secret', file=sys.stderr); print('{\"error\":\"fixture-secret\"}')")]
    [InlineData("print('invalid fixture-secret')")]
    [InlineData("print('x' * 70000)")]
    public async Task Invalid_oversized_and_provider_errors_are_sanitized(string script)
    {
        using var helper = new Helper(script);
        var error = await Assert.ThrowsAsync<SandboxRuntimeException>(() => helper.Client.CallAsync("status", new { }));
        Assert.DoesNotContain("fixture-secret", error.ToString());
    }

    [Fact]
    public async Task Cancellation_stops_a_helper_waiting_for_cloud_response()
    {
        using var helper = new Helper("import json, sys, time; json.load(sys.stdin); time.sleep(60)");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var task = helper.Client.CallAsync("launch", new { }, timeout.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class Helper : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "modal-client-test-" + Guid.NewGuid().ToString("N"));
        public ModalClient Client { get; }
        public Helper(string script)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "helper.py"); File.WriteAllText(path, script);
            Client = new(new() { AppName = "fixture", OwnerId = "fixture", Environment = "testing", Script = path },
                _ => Task.FromResult(new ModalCredentials("fixture-id", "fixture-secret")));
        }
        public void Dispose() => Directory.Delete(directory, true);
    }
}
