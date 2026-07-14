namespace Mintokei.AgentEngine.AgentTools;

public record ModelFieldOverride
{
    public bool? Visible { get; init; }
    public List<string>? AllowedValues { get; init; }
    public string? Default { get; init; }
}

public record AgentToolModel
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool IsDefault { get; init; }
    public bool Hidden { get; init; }
    public Dictionary<string, ModelFieldOverride>? FieldOverrides { get; init; }
}

public record AgentToolModelList
{
    public required AgentToolKey AgentToolKey { get; init; }
    public required List<AgentToolModel> Models { get; init; }
    public required string Source { get; init; }
    public required DateTimeOffset FetchedAt { get; init; }
}
