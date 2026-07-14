namespace Mintokei.Runner.Host.Server;

public record RequestRunnerTokenCommand(
    Guid MachineId,
    string Secret);

public record RequestRunnerTokenResult(
    string AccessToken,
    DateTimeOffset ExpiresAt);
