namespace Mintokei.Runner.Host.Server;

public record CreateEnrollmentTokenCommand(
    string? UserId,
    string? UserName,
    // When MachineName is set, the handler pre-creates the machine identity now (single-use sandbox
    // provisioning) and binds this token to it, so the caller knows the machine id up front and the row is
    // born marked ephemeral. Omit for a legacy floating token (machine created at enroll time).
    string? MachineName = null,
    bool IsEphemeral = false,
    string? Profile = null);

public record CreateEnrollmentTokenResult(
    string Token,
    string DisplayPrefix,
    DateTimeOffset ExpiresAt,
    // The pre-created machine id, or null for a legacy floating token.
    Guid? MachineId = null);
