using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public static class EvidenceValidator
{
    public static List<string> Validate(IEnumerable<Evidence> evidence, IReadOnlyDictionary<Guid, Message> allowed, ISet<Guid>? newIds = null, bool requireNew = false)
    {
        var errors = new List<string>();
        var items = evidence.ToList();
        if (items.Count == 0)
            errors.Add("No evidence supplied");
        foreach (var e in items)
        {
            if (!allowed.TryGetValue(e.MessageId, out var m))
            {
                errors.Add($"Unknown or unauthorized source {e.MessageId}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(e.Quote) || !m.Content.Contains(e.Quote, StringComparison.Ordinal))
                errors.Add($"Quote does not occur in source {e.MessageId}");
        }

        if (requireNew && !items.Any(e => newIds!.Contains(e.MessageId)))
            errors.Add("No new message establishes this candidate");
        return errors;
    }

    public static bool ContainsSecret(string text) => Regex.IsMatch(text, @"(?i)(sk-[a-z0-9_-]{12,}|(?:password|api[_ -]?key|access[_ -]?token|secret)\s*[:=]\s*\S+|-----BEGIN .*PRIVATE KEY-----)");
    public static List<string> ValidateCandidate(Candidate c, IReadOnlyDictionary<Guid, Message> allowed, ISet<Guid> newIds, bool allowSensitive)
    {
        var errors = Validate(c.SourceEvidence, allowed, newIds, true);
        if (c.Status == "confirmed" && !c.SourceEvidence.Any(e => allowed.TryGetValue(e.MessageId, out var m) && (m.Role == "user" || m.Role == "tool" && Json.Node(m.MetadataJson)?["verifiedExecution"]?.GetValue<bool>() == true)))
            errors.Add("Assistant suggestion is not a confirmed fact");
        if (!allowSensitive && ContainsSecret(c.Text + Json.Write(c.Value)))
            errors.Add("Sensitive credential-like content rejected");
        if (c.Text.Length > 4000 || string.IsNullOrWhiteSpace(c.Text))
            errors.Add("Memory text is empty or exceeds atomic memory limit");
        return errors;
    }
}
