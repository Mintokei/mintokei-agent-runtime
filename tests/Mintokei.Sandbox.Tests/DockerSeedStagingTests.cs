using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Runner.Contracts;
using Mintokei.Runner.Contracts.Messages;
using Mintokei.Sandbox.Docker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// The local Docker backend must stage a sandbox-uid-readable COPY of the host credentials rather than
/// bind-mounting them raw.
///
/// This is a regression guard for a real failure: the sandbox runs as a non-root uid, host agent-CLI creds are
/// root-owned 0600/0700, and the container entrypoint copies /seed into HOME under `set -e` — so a raw mount
/// made the container die with "cp: Permission denied" before its runner ever started, which the control plane
/// could only report as "exited before its agent runner could connect". Kubernetes (root initContainer) and the
/// nested/remote path (per-session stager) already avoided this; local Docker was the one path that did not.
/// </summary>
public class DockerSeedStagingTests
{
    private sealed class StubCommandRunner : IRemoteCommandRunner
    {
        // Mimics the staging script: report every source it was handed as staged.
        public Task<RunCommandResponse> RunAsync(
            Guid machineId, string workingDirectory, string executable,
            IReadOnlyList<string> args, int timeoutMs, CancellationToken ct = default)
        {
            var staged = new List<string>();
            void Report(int argIndex, string name)
            {
                if (argIndex < args.Count && !string.IsNullOrEmpty(args[argIndex])) staged.Add($"STAGED {name}");
            }

            // sh -c <script> <argv0> <dir> <claude> <claude.json> <codex> <git> <uid>
            Report(4, ".claude");
            Report(5, ".claude.json");
            Report(6, ".codex");
            Report(7, "git");
            return Task.FromResult(new RunCommandResponse("", 0, string.Join('\n', staged), "", null));
        }
    }

    private static DockerSandboxRuntime NewRuntime(string root) =>
        new(NullLogger<DockerSandboxRuntime>.Instance,
            new SandboxCredentialStager(new StubCommandRunner(), Options.Create(new SandboxOptions { SeedStagingRoot = root })));

    private static SandboxSpec SpecWithRawCreds(string name) => new()
    {
        Image = "img",
        Name = name,
        RuntimeClass = "runc",
        Limits = new SandboxResources(256L * 1024 * 1024, 1, 128),
        Mounts =
        [
            new SandboxMount("/root/.claude", "/seed/.claude", ReadOnly: true),
            new SandboxMount("/root/.claude.json", "/seed/.claude.json", ReadOnly: true),
            new SandboxMount("/var/cache/repos", "/repo-cache", ReadOnly: true), // not a cred mount
        ],
    };

    [Fact]
    public async Task Credential_mounts_are_replaced_with_staged_copies()
    {
        var spec = await NewRuntime("/tmp/stage").StageSeedCredentialsAsync(SpecWithRawCreds("sbx-1"), default);

        var claude = Assert.Single(spec.Mounts, m => m.Target == "/seed/.claude");
        var claudeJson = Assert.Single(spec.Mounts, m => m.Target == "/seed/.claude.json");

        // The whole point: the container must never see the host's own root-owned credential paths.
        Assert.Equal("/tmp/stage/sbx-1/.claude", claude.Source);
        Assert.Equal("/tmp/stage/sbx-1/.claude.json", claudeJson.Source);
        Assert.DoesNotContain(spec.Mounts, m => m.Source is "/root/.claude" or "/root/.claude.json");
    }

    [Fact]
    public async Task Non_credential_mounts_are_left_alone()
    {
        var spec = await NewRuntime("/tmp/stage").StageSeedCredentialsAsync(SpecWithRawCreds("sbx-2"), default);

        var cache = Assert.Single(spec.Mounts, m => m.Target == "/repo-cache");
        Assert.Equal("/var/cache/repos", cache.Source);
    }

    [Fact]
    public async Task Spec_without_credentials_is_untouched()
    {
        // Broker-egress sessions seed no credentials at all, so staging must be a no-op rather than an error.
        var spec = new SandboxSpec
        {
            Image = "img",
            Name = "sbx-3",
            RuntimeClass = "runc",
            Limits = new SandboxResources(256L * 1024 * 1024, 1, 128),
            Mounts = [new SandboxMount("/var/cache/repos", "/repo-cache", ReadOnly: true)],
        };

        var staged = await NewRuntime("/tmp/stage").StageSeedCredentialsAsync(spec, default);

        Assert.Same(spec, staged);
    }
}
