using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mintokei.Memory;
// Application-owned callbacks for an explicitly registered, bounded tool set.
// The closure captures ownership/time boundaries; it is never serialized.
public sealed class ClientToolSession(bool persistSession, Func<string, string, JsonObject, CancellationToken, Task<JsonObject>> execute, IReadOnlyList<string>? allowedToolNames = null)
{
    public IReadOnlyList<string> AllowedToolNames { get; } = allowedToolNames ?? ["search_knowledge", "load_evidence"];
    public bool PersistSession { get; } = persistSession;

    [JsonIgnore]
    public Func<string, string, JsonObject, CancellationToken, Task<JsonObject>> Execute { get; } = execute;

    public override string ToString() => "Turn-scoped client tools";
}
