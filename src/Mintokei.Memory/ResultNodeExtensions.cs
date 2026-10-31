using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public static class ResultNodeExtensions
{
    public static LlmResult DeserializeResult(this JsonNode node) => Json.Read<LlmResult>(node.ToJsonString());
}
