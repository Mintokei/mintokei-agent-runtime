using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public record ContextAssessment(List<string> Missing, List<string> Queries);
