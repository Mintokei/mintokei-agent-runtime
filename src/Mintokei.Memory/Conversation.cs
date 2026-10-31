using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class Conversation : Owned
{
    public string Title { get; set; } = "New conversation";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "active";
    public string? CodexThreadId { get; set; }
    public long VisibleSequence { get; set; }
    public long LastProcessedSequence { get; set; }
    public bool Imported { get; set; }
    public bool ReplayAll { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public string? ConfigurationJson { get; set; }
}
