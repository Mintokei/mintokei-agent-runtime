using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record Candidate(string TemporaryId, string Type, string Text, string Subject, string Predicate, JsonNode? Value, string Status, string Scope, List<Evidence> SourceEvidence);
