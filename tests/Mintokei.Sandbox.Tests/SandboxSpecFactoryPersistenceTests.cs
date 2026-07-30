using Microsoft.Extensions.Options;
using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// A persistent workspace store backs <c>/repos</c>, so it only means anything when the session HAS repos.
///
/// This is normalized in the spec factory rather than in each backend because it had already diverged: both
/// Docker paths skipped the store for a repo-less session, while Kubernetes created the PVC whenever the key
/// was set. The same session therefore left an empty PVC on one backend and nothing on the other — and the
/// embedder's GC, which lists keys off the backend, was handed a key that never had content.
///
/// Deciding it once, where all three backends converge, is what stops that being a per-backend question again.
/// </summary>
public class SandboxSpecFactoryPersistenceTests
{
    private static SandboxSpecFactory Factory() =>
        new(Options.Create(new SandboxOptions
        {
            Image = "img:1",
            AllowedProfiles = ["standard"],
            Profiles = { ["standard"] = new SandboxProfileConfig() },
        }));

    private static SandboxProfile Profile() =>
        new("standard", "runc", new SandboxResources(4L * 1024 * 1024 * 1024, 2, 512), SandboxEgress.Open, null);

    private static SandboxSessionRequest Request(Guid? key, params string[] repos) => new()
    {
        BackendUrl = "https://api",
        EnrollmentToken = "tok",
        Name = "sbx-1",
        PersistentWorkspaceKey = key,
        Repos = [.. repos.Select(r => new SandboxRepoSpec(r))],
    };

    [Fact]
    public void Keeps_the_key_when_the_session_has_a_working_tree()
    {
        var key = Guid.NewGuid();

        var spec = Factory().Build(Profile(), Request(key, "https://example.invalid/r.git"));

        Assert.Equal(key, spec.PersistentWorkspaceKey);
        Assert.True(spec.Env.ContainsKey(SandboxSpecFactory.ReposEnvVar));
    }

    [Fact]
    public void Drops_the_key_when_the_session_has_no_repos()
    {
        // Every backend now sees the same spec, so none of them creates a store with nothing in it — and the
        // GC never sees a key one backend would have created and another would not.
        var spec = Factory().Build(Profile(), Request(Guid.NewGuid()));

        Assert.Null(spec.PersistentWorkspaceKey);
        Assert.False(spec.Env.ContainsKey(SandboxSpecFactory.ReposEnvVar));
    }

    [Fact]
    public void Warm_repo_agnostic_sandboxes_never_carry_a_workspace_store()
    {
        // A warm pool sandbox is repo-agnostic by definition: it is claimed by a session later. Persisting a
        // tree it does not have yet would allocate storage per warm slot for nothing.
        Assert.Null(Factory().Build(Profile(), Request(key: Guid.NewGuid())).PersistentWorkspaceKey);
    }

    [Fact]
    public void No_key_stays_no_key()
    {
        Assert.Null(Factory().Build(Profile(), Request(key: null, "https://example.invalid/r.git")).PersistentWorkspaceKey);
    }
}
