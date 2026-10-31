using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public record RetrievalPlan(string Intent, List<string> RequiredInformation, List<string> Queries, List<ArtifactRequest>? ArtifactRequests = null, List<Guid>? ProvisionalMessageIds = null);
