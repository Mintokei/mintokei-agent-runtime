using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public record ArtifactSelection(List<ArtifactVersion> Versions, List<string> Missing);
