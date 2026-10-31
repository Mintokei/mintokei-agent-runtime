using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record StateChange(string Operation, string Path, JsonNode? Value, List<Guid> SourceMessageIds);
