using System.Security.Cryptography;
using Mintokei.Runner.Host.Domain.Machines;
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

        var enrollmentToken = new EnrollmentToken
        {
            Id = Guid.NewGuid(),
            TokenHash = tokenHash,
            DisplayPrefix = displayPrefix,
            ExpiresAt = expiresAt,
            IsUsed = false,
            CreatedByUserId = command.UserId,
            CreatedByUserName = command.UserName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.EnrollmentTokens.Add(enrollmentToken);
        await db.SaveChangesAsync();

        return RunnerHostResult<CreateEnrollmentTokenResult>.Ok(new(
            plaintextToken,
            displayPrefix,
            expiresAt));
    }
}
