using System.Text.RegularExpressions;

namespace Mintokei.Memory;
// These records are populated by the application after real operations, never by the LLM.
public record SearchAttempt(string Source, string Scope, string Status, string Query, int? CandidateCount, Guid TraceSpanId, string? Error = null);
