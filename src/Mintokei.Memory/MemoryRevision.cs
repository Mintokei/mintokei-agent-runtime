using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class MemoryRevision : Row
{
    public Guid MemoryId { get; set; }
    public int Version { get; set; }
    public string Operation { get; set; } = "CREATE";
    public string SnapshotJson { get; set; } = "{}";
    public string Reason { get; set; } = "";
    public Guid RunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
