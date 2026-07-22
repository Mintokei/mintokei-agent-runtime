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
        // Non-root: /data tmpfs is owned by the agent uid so the runner can write --data-dir.
        Assert.Contains($"/data:uid={SandboxImage.AgentUid},gid={SandboxImage.AgentUid},mode=0700", a);
    }

    [Fact]
    public void No_read_only_rootfs_by_default()
        => Assert.DoesNotContain("--read-only", DockerCommand.BuildRunArgs(Spec()).ToList());

    [Fact]
    public void ReadOnlyRootfs_adds_read_only_and_writable_tmpfs()
    {
        var spec = Spec() with { ReadOnlyRootfs = true, Tmpfs = ["/data", SandboxImage.AgentHome, "/tmp", "/repos"] };
        var a = DockerCommand.BuildRunArgs(spec).ToList();
        Assert.Contains("--read-only", a);
        Assert.Contains($"{SandboxImage.AgentHome}:uid={SandboxImage.AgentUid},gid={SandboxImage.AgentUid},mode=0700", a);
        Assert.Contains($"/tmp:uid={SandboxImage.AgentUid},gid={SandboxImage.AgentUid},mode=0700", a);
        Assert.Contains($"/repos:uid={SandboxImage.AgentUid},gid={SandboxImage.AgentUid},mode=0700", a);
    }

    [Fact]
    public void Tmpfs_defers_to_a_real_mount_at_the_same_path()
    {
        // /repos requested as tmpfs AND mounted as the persisted volume → the volume wins, no tmpfs at /repos
        // (Docker rejects a tmpfs + volume at one path).
        var spec = Spec() with
        {
            Tmpfs = ["/data", "/repos"],
            Mounts = [new SandboxMount("mintokei-ws-x", "/repos", ReadOnly: false)],
        };
        var a = DockerCommand.BuildRunArgs(spec).ToList();
        Assert.Contains("mintokei-ws-x:/repos", a);                     // the volume mount is present
        Assert.DoesNotContain(a, s => s.StartsWith("/repos:uid="));     // …and no tmpfs at /repos
        Assert.Contains($"/data:uid={SandboxImage.AgentUid},gid={SandboxImage.AgentUid},mode=0700", a); // /data still tmpfs
    }

    [Fact]
    public void Broker_egress_fails_closed()
    {
        // The enforcing per-session broker isn't wired yet, so the backend must refuse rather than launch an
        // unenforced "brokered" container (open network + no creds).
        var spec = Spec() with { Egress = SandboxEgress.Broker, EgressAllowlist = ["github.com"] };
        var ex = Assert.Throws<SandboxRuntimeException>(() => DockerCommand.BuildRunArgs(spec));
        Assert.Contains("fail-closed", ex.Message);
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

    [Fact]
    public void Carries_read_only_rootfs_from_config()
    {
        var o = new SandboxOptions
        {
            DefaultProfile = "hardened",
            AllowedProfiles = ["hardened"],
            Profiles = { ["hardened"] = new SandboxProfileConfig { Runtime = "runc", ReadOnlyRootfs = true } },
        };

        Assert.True(Resolver(o).Resolve().ReadOnlyRootfs);
    }

    [Fact]
    public void Read_only_rootfs_off_by_default()
    {
        var o = new SandboxOptions { DefaultProfile = "standard", AllowedProfiles = ["standard"] };
        Assert.False(Resolver(o).Resolve().ReadOnlyRootfs);
    }

    [Fact]
    public void Maps_broker_egress_and_carries_allowlist()
    {
        var o = new SandboxOptions
        {
            DefaultProfile = "hardened",
            AllowedProfiles = ["hardened"],
            Profiles =
            {
                ["hardened"] = new SandboxProfileConfig
                {
                    Runtime = "runsc",
                    Egress = "broker",
                    EgressAllowlist = ["github.com", "api.anthropic.com"],
                },
            },
        };

        var p = Resolver(o).Resolve();

        Assert.Equal(SandboxEgress.Broker, p.Egress);
        Assert.Equal(["github.com", "api.anthropic.com"], p.EgressAllowlist);
    }

    [Fact]
    public void Allowlist_empty_when_not_broker()
    {
        var o = new SandboxOptions
        {
            DefaultProfile = "standard",
            AllowedProfiles = ["standard"],
            // An allowlist on a non-broker profile is inert — it only takes effect under broker egress.
            Profiles = { ["standard"] = new SandboxProfileConfig { Egress = "open", EgressAllowlist = ["github.com"] } },
        };

        var p = Resolver(o).Resolve();

        Assert.Equal(SandboxEgress.Open, p.Egress);
        Assert.Empty(p.EgressAllowlist);
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

    [Fact]
    public void ReadOnlyRootfs_profile_sets_the_flag_and_expands_writable_tmpfs()
    {
        var factory = new SandboxSpecFactory(Options.Create(new SandboxOptions { Image = "img:1" }));
        var profile = new SandboxProfile("hardened", "runc", new SandboxResourceLimits(1, 1, 1),
            SandboxEgress.Open, null, ReadOnlyRootfs: true);

        var spec = factory.Build(profile, new SandboxSessionRequest
        {
            BackendUrl = "https://api", EnrollmentToken = "tok", Name = "sess-1",
        });

        Assert.True(spec.ReadOnlyRootfs);
        Assert.Contains("/data", spec.Tmpfs);
        Assert.Contains(SandboxImage.AgentHome, spec.Tmpfs);
        Assert.Contains("/tmp", spec.Tmpfs);
        Assert.Contains(SandboxSpecFactory.RepoRoot, spec.Tmpfs);
    }

    [Fact]
    public void Writable_rootfs_profile_keeps_only_the_data_tmpfs()
    {
        var factory = new SandboxSpecFactory(Options.Create(new SandboxOptions { Image = "img:1" }));
        var profile = new SandboxProfile("standard", "runc", new SandboxResourceLimits(1, 1, 1), SandboxEgress.Open, null);

        var spec = factory.Build(profile, new SandboxSessionRequest
        {
            BackendUrl = "https://api", EnrollmentToken = "tok", Name = "sess-1",
        });

        Assert.False(spec.ReadOnlyRootfs);
        Assert.Equal(["/data"], spec.Tmpfs);
    }

    private static SandboxProfile BrokerProfile(params string[] allowlist) =>
        new("hardened", "runsc", new SandboxResourceLimits(1, 1, 1), SandboxEgress.Broker, null)
        {
            EgressAllowlist = allowlist,
        };

    [Fact]
    public void Broker_mode_omits_credential_seed_mounts_and_carries_allowlist()
    {
        var factory = new SandboxSpecFactory(Options.Create(new SandboxOptions { Image = "img:1" }));

        var spec = factory.Build(BrokerProfile("github.com"), new SandboxSessionRequest
        {
            BackendUrl = "https://api",
            EnrollmentToken = "tok",
            Name = "sess-1",
            // All creds provided — broker mode must still refuse to seed any of them into the box.
            ClaudeConfigHostDir = "/root/.claude",
            ClaudeConfigJsonHostFile = "/root/.claude.json",
            CodexConfigHostDir = "/root/.codex",
            GitCredentialsHostDir = "/root/git-creds",
            // A non-credential RO mirror is still allowed through.
            Repos = [new SandboxRepoSpec("https://github.com/acme/app.git")],
            RepoCacheHostPath = "/cache",
        });

        Assert.Equal(SandboxEgress.Broker, spec.Egress);
        Assert.Equal(["github.com"], spec.EgressAllowlist);
        Assert.DoesNotContain(spec.Mounts, m => m.Target.StartsWith("/seed"));  // no secret seeded
        Assert.Contains(spec.Mounts, m => m is { Target: "/repo-cache", ReadOnly: true }); // mirror still mounted
    }

    [Fact]
    public void Broker_mode_without_allowlist_throws()
    {
        var factory = new SandboxSpecFactory(Options.Create(new SandboxOptions { Image = "img:1" }));

        var ex = Assert.Throws<SandboxRuntimeException>(() => factory.Build(BrokerProfile(), new SandboxSessionRequest
        {
            BackendUrl = "https://api", EnrollmentToken = "tok", Name = "sess-1",
        }));

        Assert.Contains("EgressAllowlist is empty", ex.Message);
    }

    [Fact]
    public void Broker_mode_with_host_gateway_throws()
    {
        var factory = new SandboxSpecFactory(Options.Create(new SandboxOptions { Image = "img:1" }));

        var ex = Assert.Throws<SandboxRuntimeException>(() => factory.Build(BrokerProfile("github.com"),
            new SandboxSessionRequest
            {
                BackendUrl = "https://api", EnrollmentToken = "tok", Name = "sess-1", AddHostGateway = true,
            }));

        Assert.Contains("AddHostGateway", ex.Message);
    }
}
