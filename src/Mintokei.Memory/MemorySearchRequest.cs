using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record MemorySearchRequest(SearchScope Scope, string Query, int Limit);
