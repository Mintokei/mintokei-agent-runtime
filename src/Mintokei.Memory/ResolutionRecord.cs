using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public class ResolutionRecord : Owned
{
    public Guid ConversationId { get; set; }
    public Guid TurnId { get; set; }
    public Guid SourceMessageId { get; set; }
    public long ThroughSequence { get; set; }
    public string Expression { get; set; } = "";
    public string Status { get; set; } = "not_found";
    public string ResultJson { get; set; } = "{}";
    public Guid TraceId { get; set; }
}
