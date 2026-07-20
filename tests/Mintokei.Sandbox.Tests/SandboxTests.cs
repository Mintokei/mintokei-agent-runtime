using Microsoft.Extensions.Options;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Docker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class DockerCommandTests
{
    private static SandboxSpec Spec() => new()
    {
        Image = "mintokei/sandbox:latest",
        Name = "sess-1",
        RuntimeClass = "runc",
        Limits = new SandboxResourceLimits(4L * 1024 * 1024 * 1024, 2, 512),
        Mounts = [new SandboxMount("/repo-cache", "/repo-cache", ReadOnly: true)],
        Env = new Dictionary<string, string> { ["SANDBOX_REPO_URL"] = "https://x/y.git" },
        Args = ["--backend", "https://api", "--token", "tok", "--name", "sess-1"],
    };

    [Fact]
    public void Image_precedes_runner_flags()
    {
        var a = DockerCommand.BuildRunArgs(Spec()).ToList();
        var image = a.IndexOf("mintokei/sandbox:latest");
        var backend = a.IndexOf("--backend");
        Assert.True(image >= 0 && backend > image, "runner flags must be appended after the image");
    }

    [Fact]
    public void Maps_runtime_limits_mounts_env_and_hardening()
    {
        var a = DockerCommand.BuildRunArgs(Spec()).ToList();
        Assert.Equal("runc", ValueAfter(a, "--runtime"));
        Assert.Equal("512", ValueAfter(a, "--pids-limit"));
        Assert.Contains("/repo-cache:/repo-cache:ro", a);
        Assert.Contains("SANDBOX_REPO_URL=https://x/y.git", a);
        Assert.Equal("ALL", ValueAfter(a, "--cap-drop"));
        Assert.Contains("no-new-privileges", a);
        Assert.Contains("mintokei.sandbox=1", a); // managed label for reconcile
    }

    private static string ValueAfter(List<string> a, string flag) => a[a.IndexOf(flag) + 1];
}

public class SandboxProfileResolverTests
{
    private static SandboxProfileResolver Resolver(SandboxOptions o) => new(Options.Create(o));

    [Fact]
    public void Unknown_profile_clamps_to_default()
    {
        var o = new SandboxOptions
        {
            DefaultProfile = "standard",
            AllowedProfiles = ["standard"],
            Profiles = { ["standard"] = new SandboxProfileConfig { Runtime = "runc" } },
        };

        var p = Resolver(o).Resolve(sessionOverride: "strict");

        Assert.Equal("standard", p.Name);
        Assert.Equal("runc", p.Runtime);
    }

    [Fact]
    public void Session_override_wins_when_allowed()
    {
        var o = new SandboxOptions
        {
            DefaultProfile = "standard",
            AllowedProfiles = ["standard", "isolated"],
            Profiles =
            {
                ["standard"] = new SandboxProfileConfig(),
                ["isolated"] = new SandboxProfileConfig { Runtime = "runsc" },
            },
        };

        var p = Resolver(o).Resolve(sessionOverride: "isolated", workspaceDefault: "standard");

        Assert.Equal("isolated", p.Name);
        Assert.Equal("runsc", p.Runtime);
    }

    [Fact]
    public void Missing_profile_config_falls_back_to_builtin_standard()
    {
        var o = new SandboxOptions { DefaultProfile = "standard", AllowedProfiles = ["standard"] };

        var p = Resolver(o).Resolve();

        Assert.Equal("standard", p.Name);
        Assert.Equal("runc", p.Runtime); // built-in default
    }
}

public class SandboxSpecFactoryTests
{
    [Fact]
    public void Encodes_runner_flags_repo_env_and_cred_mounts()
    {
        var factory = new SandboxSpecFactory(Options.Create(new SandboxOptions { Image = "img:1" }));
        var profile = new SandboxProfile("standard", "runc", new SandboxResourceLimits(1, 1, 1), SandboxEgress.Open, null);

        var spec = factory.Build(profile, new SandboxSessionRequest
        {
            BackendUrl = "https://api",
            EnrollmentToken = "tok",
            Name = "sess-1",
            Repos = [new SandboxRepoSpec("https://github.com/acme/app.git")],
            RepoCacheHostPath = "/cache",
            ClaudeConfigHostDir = "/root/.claude",
        });

        Assert.Equal("img:1", spec.Image);
        Assert.Contains("--token", spec.Args);
        // One repo → SANDBOX_REPOS carries "url|sourcePath|branch" (branch empty), sourcePath defaulted.
        Assert.Equal("https://github.com/acme/app.git|/repos/app|", spec.Env["SANDBOX_REPOS"]);
        Assert.Contains(spec.Mounts, m => m is { Target: "/repo-cache", ReadOnly: true });
        Assert.Contains(spec.Mounts, m => m is { Target: "/seed/.claude", ReadOnly: true });
    }

    [Fact]
    public void Encodes_multiple_repos_into_SANDBOX_REPOS()
    {
        var factory = new SandboxSpecFactory(Options.Create(new SandboxOptions { Image = "img:1" }));
        var profile = new SandboxProfile("standard", "runc", new SandboxResourceLimits(1, 1, 1), SandboxEgress.Open, null);

        var spec = factory.Build(profile, new SandboxSessionRequest
        {
            BackendUrl = "https://api",
            EnrollmentToken = "tok",
            Name = "sess-1",
            Repos =
            [
                new SandboxRepoSpec("https://github.com/acme/api.git", Branch: "main"),
                new SandboxRepoSpec("https://github.com/acme/web.git", SourcePath: "/repos/webapp"),
            ],
        });

        // ';'-separated records, each 'url|sourcePath|branch'. First repo's branch set, second's path overridden.
        Assert.Equal(
            "https://github.com/acme/api.git|/repos/api|main;https://github.com/acme/web.git|/repos/webapp|",
            spec.Env["SANDBOX_REPOS"]);
    }

    [Fact]
    public void Mounts_git_credentials_read_only_when_set()
    {
        var factory = new SandboxSpecFactory(Options.Create(new SandboxOptions { Image = "img:1" }));
        var profile = new SandboxProfile("standard", "runc", new SandboxResourceLimits(1, 1, 1), SandboxEgress.Open, null);

        var spec = factory.Build(profile, new SandboxSessionRequest
        {
            BackendUrl = "https://api",
            EnrollmentToken = "tok",
            Name = "sess-1",
            GitCredentialsHostDir = "/root/git-creds",
        });

        Assert.Contains(spec.Mounts, m => m is { Source: "/root/git-creds", Target: "/seed/git", ReadOnly: true });
    }

    [Theory]
    [InlineData("https://github.com/acme/app.git", "/repos/app")]
    [InlineData("git@github.com:acme/app.git", "/repos/app")]
    [InlineData("https://host/team/repo", "/repos/repo")]
    public void DefaultSourcePath_derives_repo_name(string url, string expected)
        => Assert.Equal(expected, SandboxSpecFactory.DefaultSourcePath(url));
}
