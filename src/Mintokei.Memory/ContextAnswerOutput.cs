using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public record ContextAnswerOutput(List<ResolvedReference> Resolutions, List<string> Missing, List<string> Queries, string Disposition, string? Answer, List<AnswerEvidence> AnswerEvidence);
