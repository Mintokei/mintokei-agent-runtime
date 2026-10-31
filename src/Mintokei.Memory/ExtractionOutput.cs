using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record ExtractionOutput(List<Candidate> MemoryCandidates, List<StateChange> StateChanges, List<ReferenceRequest> UnresolvedReferences);
