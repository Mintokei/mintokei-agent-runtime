using System.Security.Cryptography;
using Mintokei.Runner.Host.Domain.Machines;
using Mintokei.Runner.Host.Domain.Machines.Enums;
using Mintokei.Runner.Host.Persistence;

namespace Mintokei.Runner.Host.Server;

public sealed class CreateEnrollmentTokenHandler(RunnerHostDbContext db)
{
    public async Task<RunnerHostResult<CreateEnrollmentTokenResult>> ExecuteAsync(CreateEnrollmentTokenCommand command)
    {
        var plaintextToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = SecretHasher.Hash(plaintextToken);
        var displayPrefix = plaintextToken[..8];
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);

        Guid? preassignedMachineId = null;
        if (command.MachineName is { } machineName)
        {
            // Pre-create the machine identity so the caller knows the id immediately and the row is born
            // marked ephemeral; enrollment will redeem into this same machine rather than create a new one.
            var machine = new RunnerMachine
            {
                Id = Guid.NewGuid(),
                Name = machineName,
                IsEphemeral = command.IsEphemeral,
                Profile = command.Profile,
                Status = RunnerMachineStatus.Offline,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.RunnerMachines.Add(machine);
            preassignedMachineId = machine.Id;
        }

        var enrollmentToken = new EnrollmentToken
        {
            Id = Guid.NewGuid(),
            TokenHash = tokenHash,
            DisplayPrefix = displayPrefix,
            ExpiresAt = expiresAt,
            IsUsed = false,
            PreassignedMachineId = preassignedMachineId,
            CreatedByUserId = command.UserId,
            CreatedByUserName = command.UserName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.EnrollmentTokens.Add(enrollmentToken);
        await db.SaveChangesAsync();

        return RunnerHostResult<CreateEnrollmentTokenResult>.Ok(new(
            plaintextToken,
            displayPrefix,
            expiresAt,
            preassignedMachineId));
    }
}
