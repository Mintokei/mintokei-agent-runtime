using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record MemoryWriteResult(Guid MemoryId, string? ProviderId);
