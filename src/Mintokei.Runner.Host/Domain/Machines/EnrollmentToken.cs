namespace Mintokei.Runner.Host.Domain.Machines;

public class EnrollmentToken
{
    public Guid Id { get; set; }
    public required string TokenHash { get; set; }
    public required string DisplayPrefix { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsUsed { get; set; }
    public Guid? UsedByMachineId { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public string? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
