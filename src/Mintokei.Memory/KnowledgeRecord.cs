using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class KnowledgeRecord : Owned
{
    public string Kind { get; set; } = "goal"; // goal, entity, topic, relationship, episode, goal_event
    public string Name { get; set; } = "";
    public string Status { get; set; } = "active";
    public Guid? ConversationId { get; set; }
    public Guid? MemoryId { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
