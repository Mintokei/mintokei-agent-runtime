using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class StateSnapshot : Row
{
    public Guid ConversationId { get; set; }
    public Guid TurnId { get; set; }
    public int Version { get; set; }
    public long ProcessedThroughSeq { get; set; }
    public string StateJson { get; set; } = "{}";
    public string DeltaJson { get; set; } = "[]";
    public string BeforeJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
