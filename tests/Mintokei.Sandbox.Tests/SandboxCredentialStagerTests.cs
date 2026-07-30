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
        Assert.Contains("10001", call.Args);                 // default: chown to the sandbox uid ($6)
    }

    [Fact]
    public async Task Stage_chowns_to_the_requested_uid_so_the_broker_can_read_its_creds()
    {
        var fake = new FakeCommandRunner { Handler = (_, _) => new RunCommandResponse("", 0, "STAGED .claude\n", "", null) };

        await New(fake).StageAsync(Guid.NewGuid(), "sbx-brk",
            new SandboxSeedSources("/root/.claude", null, null, null), CancellationToken.None,
            uid: SandboxImage.BrokerUid);

        Assert.Contains("10002", Assert.Single(fake.Calls).Args); // nested broker mode → chown to the broker uid
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

    // --- Sweep: the collector for copies RemoveAsync missed. A staged copy is a real credential, and on the
    // nested path a leaked one is kept permanently VALID by the broker token-sync, so this is the backstop.

    [Fact]
    public async Task Sweep_passes_the_root_grace_window_and_live_names_positionally()
    {
        var fake = new FakeCommandRunner { Handler = (_, _) => new RunCommandResponse("", 0, "", "", null) };

        await New(fake, root: "/var/seed").SweepAsync(
            Guid.NewGuid(), ["sbx-live-1", "sbx-live-2"], minimumAgeMinutes: 7, ct: CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("sh", call.Exe);
        Assert.Contains("mintokei-sweep-seed", call.Args); // $0 label
        Assert.Contains("/var/seed", call.Args);           // $1 root
        Assert.Contains("7", call.Args);                   // $2 grace window
        Assert.Contains("sbx-live-1", call.Args);          // $3.. live names — args, never interpolated
        Assert.Contains("sbx-live-2", call.Args);
    }

    [Fact]
    public async Task Sweep_sanitizes_live_names_so_they_match_the_dirs_Stage_created()
    {
        var fake = new FakeCommandRunner { Handler = (_, _) => new RunCommandResponse("", 0, "", "", null) };

        // Stage writes the SANITIZED segment; comparing raw names would fail to match and delete a live
        // session's credentials out from under it.
        await New(fake).SweepAsync(Guid.NewGuid(), ["sbx/../evil"], ct: CancellationToken.None);

        var args = Assert.Single(fake.Calls).Args;
        Assert.DoesNotContain("sbx/../evil", args);
        Assert.Contains("sbx____evil", args);
    }

    [Fact]
    public async Task Sweep_counts_the_removals_the_script_reports()
    {
        var fake = new FakeCommandRunner
        {
            Handler = (_, _) => new RunCommandResponse("", 0, "SWEPT a\nSWEPT b\nnoise\n", "", null),
        };

        Assert.Equal(2, await New(fake).SweepAsync(Guid.NewGuid(), [], ct: CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_reports_nothing_on_failure_rather_than_claiming_removals()
    {
        // A non-zero exit means the sweep did not do what it says; counting the markers anyway would report
        // credentials as collected when they are still on disk.
        var fake = new FakeCommandRunner { Handler = (_, _) => new RunCommandResponse("", 1, "SWEPT a\n", "boom", null) };

        Assert.Equal(0, await New(fake).SweepAsync(Guid.NewGuid(), [], ct: CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_swallows_runner_errors()
    {
        var fake = new FakeCommandRunner { Handler = (_, _) => throw new InvalidOperationException("disconnected") };

        // Runs from reconcile paths that must not be taken down by an unreachable worker.
        Assert.Equal(0, await New(fake).SweepAsync(Guid.NewGuid(), [], ct: CancellationToken.None));
    }
}
