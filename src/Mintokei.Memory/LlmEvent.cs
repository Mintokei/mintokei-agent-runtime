using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record LlmEvent(string Type, JsonNode? Data);
