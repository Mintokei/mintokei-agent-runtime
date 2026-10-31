using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public record AnswerEvidence(Guid SourceId, string Quote);
