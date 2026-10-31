using System.Text.Json.Serialization;

namespace Mintokei.Memory;

/// <summary>Retrieval boundary selected by the host, independent of message wording.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemorySearchScope>))]
public enum MemorySearchScope
{
    AllAuthorizedHistory,
    CurrentTask
}
