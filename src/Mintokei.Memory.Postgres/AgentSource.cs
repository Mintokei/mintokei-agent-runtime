using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Mintokei.Memory.Postgres;

public record AgentSource(Guid Id, string Type, string Content, DateTimeOffset Timestamp, Guid? ConversationId = null, long? SequenceNumber = null, string? Role = null, bool Provisional = false);
