using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mintokei.Memory;

public record ReferenceRequest(string ReferenceId, Guid SourceMessageId, string Expression, string Query, string Scope, string TemporalHint);
