using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public record ArtifactRequest(string ArtifactId, string Selection, string? VersionId = null);
