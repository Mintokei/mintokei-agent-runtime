using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record SearchCandidate(Guid SourceId, string SourceType, Guid? ConversationId, long? SequenceNumber, DateTimeOffset Timestamp, string ContentExcerpt, double RetrievalScore, string RetrievalMethod, string MetadataJson = "{}");
