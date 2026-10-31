namespace Mintokei.Memory;

public record ContextEvaluation(List<ResolvedReference> Resolutions, List<string> Missing, List<string> Queries,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] EvidenceSelection? Selection = null);
