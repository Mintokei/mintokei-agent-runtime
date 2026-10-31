using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class Job : Row
{
    public Guid ConversationId { get; set; }
    public Guid TurnId { get; set; }
    public long ThroughSequence { get; set; }
    public string Kind { get; set; } = "extract";
    public string Key { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? LeaseToken { get; set; }
    public string? Error { get; set; }
    public string PayloadJson { get; set; } = "{}";
}
