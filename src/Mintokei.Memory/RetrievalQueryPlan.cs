using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public record RetrievalQueryPlan(List<string> Queries, bool UsedFallback, string Reason);
