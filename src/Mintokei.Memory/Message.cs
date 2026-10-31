using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class Message : Row
{
    public Guid ConversationId { get; set; }
    public Guid TurnId { get; set; }
    public long SequenceNumber { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string ContentType { get; set; } = "text/plain";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? AgentId { get; set; }
    public string MetadataJson { get; set; } = "{}";
}
