using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record EvidenceSelection(List<Guid> MemoryIds, List<Guid> MessageIds);
