using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class ArtifactVersion : Owned
{
    public Guid ArtifactId { get; set; }
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "markdown";
    public string Status { get; set; } = "draft";
    public string StoragePath { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public Guid? ConversationId { get; set; }
    public Guid? TurnId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
