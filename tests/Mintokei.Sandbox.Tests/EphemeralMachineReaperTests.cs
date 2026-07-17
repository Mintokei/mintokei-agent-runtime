using Mintokei.Sandbox;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class EphemeralMachineReaperTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    private static SandboxMachine Machine(string name, bool ephemeral, bool offline, TimeSpan ago)
        => new(Guid.NewGuid(), name, ephemeral, offline, Now - ago);

    private static IReadOnlyList<Guid> Prune(IReadOnlySet<string> live, params SandboxMachine[] machines)
        => EphemeralMachineReaper.SelectPrunable(machines, live, Now, Grace);

    [Fact]
    public void Prunes_ephemeral_offline_gone_and_past_grace()
    {
        var m = Machine("s1", ephemeral: true, offline: true, ago: TimeSpan.FromMinutes(30));
        Assert.Equal([m.Id], Prune(new HashSet<string>(), m));
    }

    [Fact]
    public void Keeps_persistent_online_and_within_grace()
    {
        var persistent = Machine("p", ephemeral: false, offline: true, ago: TimeSpan.FromHours(1));
        var online = Machine("on", ephemeral: true, offline: false, ago: TimeSpan.FromHours(1));
        var fresh = Machine("fresh", ephemeral: true, offline: true, ago: TimeSpan.FromMinutes(2));

        Assert.Empty(Prune(new HashSet<string>(), persistent, online, fresh));
    }

    [Fact]
    public void Keeps_a_machine_whose_container_is_still_running()
    {
        var m = Machine("live", ephemeral: true, offline: true, ago: TimeSpan.FromHours(1));
        // "live" appears in the runtime's managed set → may reconnect and resume → not prunable.
        Assert.Empty(Prune(new HashSet<string> { "live" }, m));
    }

    [Fact]
    public void Selects_only_the_gone_ones_from_a_mix()
    {
        var gone = Machine("gone", ephemeral: true, offline: true, ago: TimeSpan.FromMinutes(30));
        var live = Machine("live", ephemeral: true, offline: true, ago: TimeSpan.FromMinutes(30));

        var pruned = Prune(new HashSet<string> { "live" }, gone, live);

        Assert.Equal([gone.Id], pruned);
    }
}
