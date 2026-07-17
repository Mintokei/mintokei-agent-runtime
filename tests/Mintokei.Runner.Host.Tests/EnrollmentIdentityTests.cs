using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mintokei.Runner.Host.Domain.Machines.Enums;
using Mintokei.Runner.Host.Persistence;
using Mintokei.Runner.Host.Server;
using Xunit;

namespace Mintokei.Runner.Host.Tests;

/// <summary>
/// Tests the pre-assigned machine identity path in enrollment: a provisioning token pre-creates the machine
/// (returning its id, marked ephemeral) and enrollment redeems INTO that same identity; a legacy floating
/// token still creates its machine at enroll time.
/// </summary>
public class EnrollmentIdentityTests : IAsyncLifetime
{
    private SqliteConnection _keepAlive = null!;
    private string _cs = null!;

    public async ValueTask InitializeAsync()
    {
        _cs = $"Data Source=file:enroll_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_cs);
        _keepAlive.Open();
        await using var db = NewDb();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _keepAlive.DisposeAsync();

    private RunnerHostDbContext NewDb()
        => new(new DbContextOptionsBuilder<RunnerHostDbContext>().UseSqlite(_cs).Options);

    [Fact]
    public async Task Legacy_token_has_no_preassigned_machine_and_creates_one_at_enroll()
    {
        var create = await new CreateEnrollmentTokenHandler(NewDb())
            .ExecuteAsync(new CreateEnrollmentTokenCommand("u", "user"));
        Assert.Null(create.Value!.MachineId); // floating token — no machine yet

        await using (var db = NewDb())
            Assert.Empty(db.RunnerMachines);

        var enroll = await new EnrollMachineHandler(NewDb())
            .ExecuteAsync(new EnrollMachineCommand(create.Value.Token, "laptop"));
        Assert.True(enroll.IsSuccess);

        await using (var check = NewDb())
        {
            var m = Assert.Single(check.RunnerMachines);
            Assert.Equal(enroll.Value!.MachineId, m.Id);
            Assert.Equal("laptop", m.Name);
            Assert.False(m.IsEphemeral);
            Assert.NotNull(m.SecretHash);
        }
    }

    [Fact]
    public async Task Provisioning_token_precreates_ephemeral_identity()
    {
        var create = await new CreateEnrollmentTokenHandler(NewDb())
            .ExecuteAsync(new CreateEnrollmentTokenCommand(
                "u", "sandbox", MachineName: "sandbox-standard-x", IsEphemeral: true, Profile: "standard"));

        var machineId = create.Value!.MachineId;
        Assert.NotNull(machineId); // identity created up front

        await using var check = NewDb();
        var m = Assert.Single(check.RunnerMachines);
        Assert.Equal(machineId, m.Id);
        Assert.Equal("sandbox-standard-x", m.Name);
        Assert.True(m.IsEphemeral);
        Assert.Equal("standard", m.Profile);
        Assert.Equal(RunnerMachineStatus.Offline, m.Status);
        Assert.Null(m.SecretHash); // not enrolled yet
    }

    [Fact]
    public async Task Enroll_redeems_into_preassigned_identity_without_creating_a_new_machine()
    {
        var create = await new CreateEnrollmentTokenHandler(NewDb())
            .ExecuteAsync(new CreateEnrollmentTokenCommand(
                "u", "sandbox", MachineName: "sandbox-standard-y", IsEphemeral: true, Profile: "standard"));
        var machineId = create.Value!.MachineId!.Value;

        var enroll = await new EnrollMachineHandler(NewDb())
            .ExecuteAsync(new EnrollMachineCommand(create.Value.Token, "sandbox-standard-y", OsInfo: "linux"));

        Assert.True(enroll.IsSuccess);
        Assert.Equal(machineId, enroll.Value!.MachineId); // SAME id — redeemed into the pre-created identity

        await using var check = NewDb();
        var m = Assert.Single(check.RunnerMachines); // still exactly one machine — no duplicate created
        Assert.Equal(machineId, m.Id);
        Assert.True(m.IsEphemeral);       // identity preserved
        Assert.Equal("standard", m.Profile);
        Assert.NotNull(m.SecretHash);     // now enrolled
        Assert.NotNull(m.EnrolledAt);
        Assert.Equal("linux", m.OsInfo);
    }
}
