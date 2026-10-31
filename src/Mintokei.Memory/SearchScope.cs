using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record SearchScope(string UserId, string Namespace, Guid ConversationId, long ThroughSequence, DateTimeOffset AsOf, bool CurrentConversationOnly = false, Guid? ExcludeMessageId = null);
