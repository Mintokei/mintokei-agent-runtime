using System.Text.Json.Serialization;

namespace Mintokei.Memory;
// A capability valid only while one application turn is executing. Never
// serialize the bearer token into prompts, traces, process arguments or logs.
public sealed class MemoryToolConnection(string url, string bearerToken, bool persistSession, string[]? toolNames = null)
{
    public string[] ToolNames { get; } = toolNames ?? ["search_knowledge", "load_evidence"];
    public string Url { get; } = url;

    [JsonIgnore]
    public string BearerToken { get; } = bearerToken;
    public bool PersistSession { get; } = persistSession;

    public override string ToString() => "Turn-scoped memory MCP connection";
}
