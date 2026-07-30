using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mintokei.Sandbox;
using Mintokei.Sandbox.Docker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

/// <summary>
/// The sweep that collects staged credential copies no live session owns.
///
/// These run the REAL shell script against a real directory, because the risk here is not the C# — it is a
/// script that deletes directories holding credentials. A unit test over the argv would pass just as happily
/// if the script removed the wrong thing.
///
/// The bug this closes was observed in production: a per-session copy of the host model token outlived its
/// session by five days, because <c>RemoveAsync</c> is best-effort and nothing collected what it missed. The
/// deployment's broker token-sync then kept rewriting the live token into that orphan, so it never decayed
/// into a useless stale token — it stayed a CURRENT credential at a predictable path.
/// </summary>
public class SandboxCredentialSweepTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mintokei-sweep-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Create a staged copy with a credential in it, aged <paramref name="ageMinutes"/> into the past.</summary>
    private string Stage(string session, int ageMinutes)
    {
        var dir = Path.Combine(_root, session);
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        File.WriteAllText(Path.Combine(dir, ".claude", ".credentials.json"), """{"claudeAiOauth":{"accessToken":"x"}}""");
        // The age test reads the SESSION dir's mtime — not the credential file's, which a token re-sync
        // touches. An orphan that is being kept fresh must still age out; that is the whole point.
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddMinutes(-ageMinutes));
        return dir;
    }

    private SandboxCredentialStager Stager() =>
        new(new LocalCommandRunner(), Options.Create(new SandboxOptions { SeedStagingRoot = _root }));

    [Fact]
    public async Task Removes_an_orphan_and_keeps_a_live_session()
    {
        var live = Stage("sbx-live", ageMinutes: 60);
        var orphan = Stage("sbx-orphan", ageMinutes: 60);

        var swept = await Stager().SweepAsync(Guid.Empty, ["sbx-live"], minimumAgeMinutes: 10);

        Assert.Equal(1, swept);
        Assert.True(Directory.Exists(live), "a live session's credentials must survive the sweep");
        Assert.False(Directory.Exists(orphan), "an orphaned credential copy must be removed");
    }

    [Fact]
    public async Task Keeps_a_copy_younger_than_the_grace_window()
    {
        // Credentials are staged BEFORE the container is created, so a sandbox mid-provision legitimately has
        // a staged copy and no container. Without the grace window the sweep would delete the credentials of
        // the very session that is starting — turning a cleanup into an outage.
        var starting = Stage("sbx-starting", ageMinutes: 1);

        var swept = await Stager().SweepAsync(Guid.Empty, [], minimumAgeMinutes: 10);

        Assert.Equal(0, swept);
        Assert.True(Directory.Exists(starting), "a copy inside the grace window must not be swept");
    }

    [Fact]
    public async Task Sweeps_an_orphan_whose_credential_file_was_just_refreshed()
    {
        // The exact production shape: the session dir is old, but a token-sync rewrote the credential inside
        // it minutes ago. Ageing on the credential file instead of the dir would keep this alive forever.
        var orphan = Stage("sbx-resynced", ageMinutes: 7 * 24 * 60);
        File.WriteAllText(Path.Combine(orphan, ".claude", ".credentials.json"), """{"claudeAiOauth":{"accessToken":"fresh"}}""");

        var swept = await Stager().SweepAsync(Guid.Empty, [], minimumAgeMinutes: 10);

        Assert.Equal(1, swept);
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public async Task Removes_every_orphan_when_nothing_is_live()
    {
        Stage("sbx-a", ageMinutes: 30);
        Stage("sbx-b", ageMinutes: 30);

        Assert.Equal(2, await Stager().SweepAsync(Guid.Empty, [], minimumAgeMinutes: 10));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Missing_staging_root_is_not_an_error()
    {
        // Nothing has been staged yet on this host — the common case on a fresh runner.
        Assert.Equal(0, await Stager().SweepAsync(Guid.Empty, ["whatever"], minimumAgeMinutes: 10));
    }

    [Fact]
    public async Task Leaves_loose_files_in_the_root_alone()
    {
        Directory.CreateDirectory(_root);
        var stray = Path.Combine(_root, "not-a-session.txt");
        File.WriteAllText(stray, "x");

        Assert.Equal(0, await Stager().SweepAsync(Guid.Empty, [], minimumAgeMinutes: 0));
        Assert.True(File.Exists(stray), "the sweep must only remove staged session directories");
    }

    /// <summary>
    /// The local backend reaches the sweep through the capability seam, using its own container inventory —
    /// which is what makes the check authoritative rather than a guess from the caller's records.
    /// </summary>
    [Fact]
    public async Task Docker_backend_exposes_the_sweep_through_the_capability()
    {
        var orphan = Stage("sbx-orphan", ageMinutes: 60);
        var live = Stage("sbx-live", ageMinutes: 60);

        ISandboxCredentialSweeper runtime = new DockerSandboxRuntime(
            NullLogger<DockerSandboxRuntime>.Instance,
            Options.Create(new SandboxOptions { SeedStagingRoot = _root }));

        Assert.Equal(1, await runtime.SweepStagedCredentialsAsync(["sbx-live"]));
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(live));
    }
}
