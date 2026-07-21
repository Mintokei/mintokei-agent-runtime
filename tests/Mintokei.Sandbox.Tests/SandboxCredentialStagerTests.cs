using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts;
using Mintokei.Runner.Contracts.Messages;
using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class RunCommandArgsTests
{
    [Fact]
    public void No_quoting_for_plain_tokens()
        => Assert.Equal("run -v /a:/b", RunCommandArgs.Encode(["run", "-v", "/a:/b"]));

    [Fact]
    public void Quotes_tokens_with_spaces()
        => Assert.Equal("\"a b\" c", RunCommandArgs.Encode(["a b", "c"]));

    [Fact]
    public void Empty_token_becomes_empty_quotes() // the stager relies on this for absent sources
        => Assert.Equal("a \"\" b", RunCommandArgs.Encode(["a", "", "b"]));

    [Fact]
    public void Escapes_embedded_quotes()
        => Assert.Contains("a\\\"b", RunCommandArgs.Encode(["a\"b"]));
}

public class SandboxCredentialStagerTests
{
    private sealed class FakeCommandRunner : IRemoteCommandRunner
    {
        public Func<string, IReadOnlyList<string>, RunCommandResponse>? Handler { get; set; }
        public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = [];

        public Task<RunCommandResponse> RunAsync(
            Guid machineId, string workingDirectory, string executable,
            IReadOnlyList<string> args, int timeoutMs, CancellationToken ct = default)
        {
            Calls.Add((executable, args));
            return Task.FromResult(Handler?.Invoke(executable, args) ?? new RunCommandResponse("", 0, "", "", null));
        }
    }

    private static SandboxCredentialStager New(FakeCommandRunner fake, string? root = null)
        => new(fake, Options.Create(new SandboxOptions { SeedStagingRoot = root }));

    [Fact]
    public async Task Stage_parses_markers_and_passes_sources_positionally()
    {
        var fake = new FakeCommandRunner { Handler = (_, _) => new RunCommandResponse("", 0, "STAGED .claude\nSTAGED git\n", "", null) };

        var staged = await New(fake).StageAsync(Guid.NewGuid(), "sbx-abc",
            new SandboxSeedSources("/root/.claude", "/root/.claude.json", "/root/.codex", "/root/creds"), CancellationToken.None);

        Assert.EndsWith("/.claude", staged.ClaudeConfigDir); // marker present → staged path returned
        Assert.EndsWith("/git", staged.GitCredentialsDir);
        Assert.Null(staged.ClaudeConfigJsonFile);            // no marker → dropped (source absent)
        Assert.Null(staged.CodexConfigDir);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("sh", call.Exe);
        Assert.Contains("mintokei-stage-seed", call.Args);   // $0 label
        Assert.Contains("/root/.claude", call.Args);         // sources handed to the script, not interpolated
        Assert.Contains("/root/creds", call.Args);
    }

    [Fact]
    public async Task Stage_uses_configured_root_and_sanitizes_the_session_segment()
    {
        var fake = new FakeCommandRunner { Handler = (_, _) => new RunCommandResponse("", 0, "STAGED .claude\n", "", null) };

        var staged = await New(fake, root: "/var/seed").StageAsync(Guid.NewGuid(), "sbx/../evil",
            new SandboxSeedSources("/x", null, null, null), CancellationToken.None);

        Assert.StartsWith("/var/seed/", staged.ClaudeConfigDir);
        Assert.DoesNotContain("..", staged.ClaudeConfigDir!); // traversal sanitized out of the path segment
    }

    [Fact]
    public async Task Stage_throws_on_nonzero_exit()
    {
        var fake = new FakeCommandRunner { Handler = (_, _) => new RunCommandResponse("", 2, "", "clone failed", null) };

        await Assert.ThrowsAsync<SandboxRuntimeException>(() =>
            New(fake).StageAsync(Guid.NewGuid(), "s", new SandboxSeedSources("/x", null, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Remove_issues_rm_under_the_root()
    {
        var fake = new FakeCommandRunner();
        await New(fake, root: "/var/seed").RemoveAsync(Guid.NewGuid(), "sbx-1", CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("rm", call.Exe);
        Assert.Contains("-rf", call.Args);
        Assert.Contains(call.Args, a => a.StartsWith("/var/seed/sbx-1"));
    }

    [Fact]
    public async Task Remove_swallows_runner_errors()
    {
        var fake = new FakeCommandRunner { Handler = (_, _) => throw new InvalidOperationException("disconnected") };
        // Best-effort cleanup path must never throw.
        await New(fake).RemoveAsync(Guid.NewGuid(), "s", CancellationToken.None);
    }
}
