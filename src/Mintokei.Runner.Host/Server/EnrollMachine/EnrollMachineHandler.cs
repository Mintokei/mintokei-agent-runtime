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

        var machine = new RunnerMachine
        {
            Id = Guid.NewGuid(),
            Name = command.Name,
            Description = command.Description,
            SecretHash = secretHash,
            SecretIssuedAt = DateTimeOffset.UtcNow,
            EnrolledAt = DateTimeOffset.UtcNow,
            Status = RunnerMachineStatus.Offline,
            OsInfo = command.OsInfo,
            RunnerVersion = command.RunnerVersion,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.RunnerMachines.Add(machine);

        // Mark enrollment token as used
        enrollmentToken.IsUsed = true;
        enrollmentToken.UsedByMachineId = machine.Id;
        enrollmentToken.UsedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();

        return RunnerHostResult<EnrollMachineResult>.Ok(new(machine.Id, plaintextSecret));
    }
}
