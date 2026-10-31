namespace Mintokei.Memory.Postgres;

/// <summary>The authorized partition for this installation. Configure in the host, not in tool calls.</summary>
public sealed class MemoryServiceOptions
{
    public string SourceSystem { get; set; } = "mintokei";
    public string OwnerId { get; set; } = "local";
    public string Namespace { get; set; } = "live";
}
