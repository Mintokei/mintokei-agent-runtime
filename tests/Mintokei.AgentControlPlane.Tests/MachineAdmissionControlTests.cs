using Xunit;

namespace Mintokei.AgentControlPlane.Tests;

/// <summary>
/// Unit tests for <see cref="MachineAdmissionControl"/> — the concurrency-critical admission core
/// (pending claims + per-machine lock) that <see cref="DefaultAgentControlPlane"/> owns for capacity enforcement.
/// This is the byte-for-byte-behaviour port of the logic <c>InMemoryAgentProcessStore</c> has today,
/// so these pin the invariants the enforcer relies on to avoid overshooting a cap.
/// </summary>
public class MachineAdmissionControlTests
{
    [Fact]
    public void AddPendingClaim_counts_until_disposed()
    {
        var machine = Guid.NewGuid();
        var admission = new MachineAdmissionControl();

        Assert.Equal(0, admission.GetPendingClaimsByMachine(machine));

        var c1 = admission.AddPendingClaim(machine, agentId: null);
        var c2 = admission.AddPendingClaim(machine, agentId: null);
        Assert.Equal(2, admission.GetPendingClaimsByMachine(machine));

        c1.Dispose();
        Assert.Equal(1, admission.GetPendingClaimsByMachine(machine));

        c2.Dispose();
        Assert.Equal(0, admission.GetPendingClaimsByMachine(machine));
    }

    [Fact]
    public void Disposing_a_claim_twice_does_not_underflow()
    {
        var machine = Guid.NewGuid();
        var admission = new MachineAdmissionControl();

        var claim = admission.AddPendingClaim(machine, agentId: null);
        admission.AddPendingClaim(machine, agentId: null);

        claim.Dispose();
        claim.Dispose();   // second dispose is a no-op — must not release the other claim's slot

        Assert.Equal(1, admission.GetPendingClaimsByMachine(machine));
    }

    [Fact]
    public void Pending_claims_are_scoped_by_machine_and_agent()
    {
        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var admission = new MachineAdmissionControl();

        admission.AddPendingClaim(m1, agent);
        admission.AddPendingClaim(m1, agentId: null);   // agentless task on the same machine
        admission.AddPendingClaim(m2, agent);

        Assert.Equal(2, admission.GetPendingClaimsByMachine(m1));
        Assert.Equal(1, admission.GetPendingClaimsByMachineAndAgent(m1, agent));
        Assert.Equal(1, admission.GetPendingClaimsByMachine(m2));
        Assert.Equal(0, admission.GetPendingClaimsByMachineAndAgent(m2, Guid.NewGuid()));
    }

    [Fact]
    public void Local_claims_live_in_the_null_machine_bucket()
    {
        var admission = new MachineAdmissionControl();

        admission.AddPendingClaim(machineId: null, agentId: null);
        admission.AddPendingClaim(machineId: null, agentId: null);

        Assert.Equal(2, admission.GetPendingClaimsByMachine(null));
    }

    [Fact]
    public void GetMachineLock_is_stable_per_machine_and_distinct_across_machines()
    {
        var machine = Guid.NewGuid();
        var admission = new MachineAdmissionControl();

        Assert.Same(admission.GetMachineLock(machine), admission.GetMachineLock(machine));
        Assert.NotSame(admission.GetMachineLock(machine), admission.GetMachineLock(Guid.NewGuid()));
        Assert.Same(admission.GetMachineLock(null), admission.GetMachineLock(null));   // local bucket too
    }

    [Fact]
    public async Task GetMachineLock_serialises_holders_on_the_same_machine()
    {
        var machine = Guid.NewGuid();
        var admission = new MachineAdmissionControl();

        var held = admission.GetMachineLock(machine);
        await held.WaitAsync();

        // A concurrent acquire of the same machine's lock can't proceed while it's held.
        var contender = admission.GetMachineLock(machine).WaitAsync();
        Assert.False(contender.IsCompleted);

        held.Release();
        await contender;   // released — now it proceeds
        Assert.True(contender.IsCompletedSuccessfully);
    }
}
