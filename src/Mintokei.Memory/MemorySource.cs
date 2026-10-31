using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class MemorySource : Row
{
    public Guid MemoryId { get; set; }
    public Guid SourceId { get; set; }
    public string SourceType { get; set; } = "message";
    public string SourceRole { get; set; } = "supports";
    public string EvidenceQuote { get; set; } = "";
    public int? EvidenceStart { get; set; }
    public Guid ExtractionRunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
