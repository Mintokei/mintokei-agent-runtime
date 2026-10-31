using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class Experiment : Owned
{
    public string Name { get; set; } = "Comparison";
    public Guid SourceConversationId { get; set; }
    public Guid ConversationAId { get; set; }
    public Guid ConversationBId { get; set; }
    public string LabelsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
