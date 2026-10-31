using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    public static string Write(object? x) => JsonSerializer.Serialize(x, Options);
    public static T Read<T>(string x) => JsonSerializer.Deserialize<T>(x, Options)!;
    public static JsonNode? Node(string? x) => x is null ? null : JsonNode.Parse(x);
}
