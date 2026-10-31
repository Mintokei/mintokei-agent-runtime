using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory.Providers;

public record ParsedCodexEvent(JsonObject Event, JsonObject? Diagnostic);
