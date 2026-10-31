using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public interface IReferenceResolutionService
{
    Task<List<ResolvedReference>> ValidateProposalsAsync(Conversation conversation, Message message, IEnumerable<ReferenceRequest> requests, IEnumerable<ResolvedReference> proposals, IReadOnlyList<Message> suppliedEvidence, IReadOnlyList<SearchCandidate> candidates, List<SearchAttempt> attempts, MemoryConfiguration config, Guid traceId, Guid parent, CancellationToken ct);
    Task<List<ResolvedReference>> ResolveAsync(Conversation conversation, Message message, IReadOnlyList<Message> recent, JsonNode state, MemoryConfiguration config, Guid traceId, CancellationToken ct);
    Task<List<ResolvedReference>> RecheckAsync(Conversation conversation, Message message, IReadOnlyList<Message> recent, JsonNode state, List<ResolvedReference> references, IReadOnlyList<Message> evidence, List<SearchAttempt> attempts, MemoryConfiguration config, Guid traceId, CancellationToken ct);
}
