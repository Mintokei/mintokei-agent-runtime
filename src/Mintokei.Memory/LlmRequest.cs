using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record LlmRequest(string Operation, OperationConfig Config, string Prompt, string? JsonSchema, string WorkingDirectory, Guid CorrelationId, string? ThreadId = null, Dictionary<string, string>? Metadata = null, string? Instructions = null, string? Input = null, JsonArray? InputItems = null, JsonArray? Tools = null, string? PreviousResponseId = null, bool StoreResponse = false, [property: System.Text.Json.Serialization.JsonIgnore] MemoryToolConnection? MemoryTools = null, [property: System.Text.Json.Serialization.JsonIgnore] ClientToolSession? ClientTools = null);
