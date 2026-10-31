using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record LlmResult(string? Output, List<JsonNode?> RawEvents, string Status, double DurationMs, string? RequestedModel, string? RequestedEffort, string? ObservedModel, string? ObservedEffort, JsonNode? Usage, int? ExitCode, string? Error, string? ThreadId, string? CliVersion, JsonNode? ProviderMetadata = null, JsonArray? ResponseItems = null);
