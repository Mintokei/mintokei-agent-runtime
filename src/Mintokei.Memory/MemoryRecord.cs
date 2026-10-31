using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class MemoryRecord : Owned
{
    public string Type { get; set; } = "fact";
    public string Subject { get; set; } = "user";
    public string Predicate { get; set; } = "";
    public string ValueJson { get; set; } = "null";
    public string CanonicalText { get; set; } = "";
    public string CanonicalHash { get; set; } = "";
    public string Status { get; set; } = "confirmed";
    public string Scope { get; set; } = "global";
    public double? Confidence { get; set; }
    public double Salience { get; set; } = 0.5;
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Version { get; set; } = 1;
    public string? ProviderMemoryId { get; set; }
    public string? EmbeddingModel { get; set; }
    public string MetadataJson { get; set; } = "{}";
}
