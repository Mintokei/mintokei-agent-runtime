using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record ResolvedReference(string ReferenceId, Guid SourceMessageId, string Expression, string Status, JsonNode? ResolvedValue, List<Evidence> Evidence, string Explanation, ReferenceRequest? SearchRequest = null, List<SearchAttempt>? SearchAttempts = null, DateTimeOffset? EvaluatedAt = null);
