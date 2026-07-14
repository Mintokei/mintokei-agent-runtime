namespace Mintokei.Runner.Host.Server;

public record CreateEnrollmentTokenCommand(
    string? UserId,
    string? UserName);

public record CreateEnrollmentTokenResult(
    string Token,
    string DisplayPrefix,
    DateTimeOffset ExpiresAt);
