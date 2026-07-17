using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Mintokei.Runner.Host.Domain.Machines;
using Mintokei.Runner.Host.Domain.Machines.Enums;
using Mintokei.Runner.Host.Persistence;

namespace Mintokei.Runner.Host.Server;

public sealed class EnrollMachineHandler(RunnerHostDbContext db)
{
    public async Task<RunnerHostResult<EnrollMachineResult>> ExecuteAsync(EnrollMachineCommand command)
    {
        var tokenHash = SecretHasher.Hash(command.EnrollmentToken);

        var enrollmentToken = await db.EnrollmentTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

        if (enrollmentToken is null)
            return RunnerHostResult<EnrollMachineResult>.BadRequest("Invalid enrollment token.");

        if (enrollmentToken.IsUsed)
            return RunnerHostResult<EnrollMachineResult>.BadRequest("Enrollment token has already been used.");

        if (enrollmentToken.ExpiresAt < DateTimeOffset.UtcNow)
            return RunnerHostResult<EnrollMachineResult>.BadRequest("Enrollment token has expired.");

        // Generate long-lived secret
        var plaintextSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var secretHash = SecretHasher.Hash(plaintextSecret);

        RunnerMachine machine;
        if (enrollmentToken.PreassignedMachineId is { } preId)
        {
            // Redeem into the machine pre-created at token time (sandbox provisioning) — keep its identity
            // (id, name, ephemeral, profile); the runner just supplies its secret + reported details.
            var existing = await db.RunnerMachines.FirstOrDefaultAsync(m => m.Id == preId);
            if (existing is null)
                return RunnerHostResult<EnrollMachineResult>.BadRequest("Pre-assigned machine no longer exists.");
            machine = existing;
        }
        else
        {
            machine = new RunnerMachine
            {
                Id = Guid.NewGuid(),
                Name = command.Name,
                Status = RunnerMachineStatus.Offline,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.RunnerMachines.Add(machine);
        }

        machine.SecretHash = secretHash;
        machine.SecretIssuedAt = DateTimeOffset.UtcNow;
        machine.EnrolledAt = DateTimeOffset.UtcNow;
        machine.OsInfo = command.OsInfo;
        machine.RunnerVersion = command.RunnerVersion;
        machine.Description ??= command.Description;

        // Mark enrollment token as used
        enrollmentToken.IsUsed = true;
        enrollmentToken.UsedByMachineId = machine.Id;
        enrollmentToken.UsedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();

        return RunnerHostResult<EnrollMachineResult>.Ok(new(machine.Id, plaintextSecret));
    }
}
