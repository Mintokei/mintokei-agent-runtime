namespace Mintokei.Runner.Host.Server;

public record EnrollMachineCommand(
    string EnrollmentToken,
    string Name,
    string? Description = null,
    string? OsInfo = null,
    string? RunnerVersion = null);

public record EnrollMachineResult(
    Guid MachineId,
    string Secret);
