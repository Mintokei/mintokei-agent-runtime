using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public static class ContextSelection
{
    public static bool IsSmallTalk(string text) => Regex.IsMatch(text.Trim(), @"^(hi|hello|hey|thanks|thank you|good morning|good evening)[!.\s]*$", RegexOptions.IgnoreCase);
    public static ArtifactSelection SelectArtifacts(IEnumerable<ArtifactVersion> versions, IEnumerable<ArtifactRequest> requests, SearchScope scope)
    {
        var allowed = versions.Where(v => v.UserId == scope.UserId && v.Namespace == scope.Namespace && v.CreatedAt <= scope.AsOf).ToList();
        var selected = new List<ArtifactVersion>();
        var missing = new List<string>();
        foreach (var request in requests.Take(3))
        {
            var candidates = Guid.TryParse(request.ArtifactId, out var id) ? allowed.Where(v => v.ArtifactId == id).OrderByDescending(v => v.Version).ToList() : [];
            var approved = candidates.FirstOrDefault(v => v.Status is "approved" or "final");
            ArtifactVersion? version = request.Selection switch
            {
                "preferred" => approved ?? candidates.FirstOrDefault(v => v.Status is "draft" or "proposed"),
                "latest_approved" => approved,
                "latest" => candidates.FirstOrDefault(),
                "latest_draft" => candidates.FirstOrDefault(v => v.Status == "draft"),
                "original" => candidates.LastOrDefault(),
                "specific" => candidates.FirstOrDefault(v => v.Id.ToString() == request.VersionId),
                _ => null
            };
            if (version == null)
                missing.Add($"No authorized {request.Selection} version available for artifact {request.ArtifactId}.");
            else if (selected.All(v => v.Id != version.Id))
                selected.Add(version);
        }

        return new(selected, missing);
    }

    // Compatibility for frozen v1 planners and deliberately disabled planning.
    // Names are matched without topic-specific document aliases or requirements.
    public static List<ArtifactRequest> LegacyArtifacts(string request, IEnumerable<string> queries, IEnumerable<ArtifactVersion> versions)
    {
        var text = string.Join(" ", queries.Append(request));
        var selection = Regex.IsMatch(request, @"\b(approved|final)\b", RegexOptions.IgnoreCase) ? "latest_approved" : Regex.IsMatch(request, @"\b(original|first version)\b", RegexOptions.IgnoreCase) ? "original" : Regex.IsMatch(request, @"\bdraft\b", RegexOptions.IgnoreCase) ? "latest_draft" : Regex.IsMatch(request, @"\b(latest|newest)\b", RegexOptions.IgnoreCase) ? "latest" : "preferred";
        return versions.Where(v => v.Name.Length > 0 && text.Contains(v.Name, StringComparison.OrdinalIgnoreCase)).DistinctBy(v => v.ArtifactId).Take(3).Select(v => new ArtifactRequest(v.ArtifactId.ToString(), selection)).ToList();
    }
}
