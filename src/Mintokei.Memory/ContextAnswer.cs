using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public static class ContextAnswer
{
    public static string? IneligibleReason(MemoryConfiguration config, bool answerRequested)
    {
        if (!answerRequested)
            return "Retrieval-only caller; no answer requested";
        if (config.AnswerMode != "combined")
            return "Separate checking and answering selected";
        if (!config.Operations["context-answer"].Enabled)
            return "Context Answer operation disabled";
        if (config.Operations["chat"].Provider != "openai_api" || config.Operations["context-answer"].Provider != "openai_api")
            return "Experimental combined answers require the OpenAI API; Codex uses its normal chat session";
        if (config.Operations["chat"].WebSearch)
            return "Tool-enabled chat uses the separate path";
        return null;
    }

    // The caller supplies only DB-authorized, time-bounded originals and loaded artifacts.
    // This verifies attribution, not whether the model's interpretation is semantically correct.
    public static Dictionary<Guid, string> Sources(JsonObject input)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var key in new[]
        {
            "recentMessages",
            "historicalEvidence",
            "conversationHistory",
            "artifacts"
        }

        )
            foreach (var row in input[key]?.AsArray() ?? [])
                if (Guid.TryParse(row?["id"]?.ToString(), out var id) && row?["content"] is { } content)
                    result[id] = content.ToString();
        return result;
    }

    public static List<string> Validate(ContextAnswerOutput output, JsonObject input, IReadOnlyList<ResolvedReference> validated)
    {
        var errors = new List<string>();
        var sources = Sources(input);
        var requests = Json.Read<List<ReferenceRequest>>(input["referencesToResolve"]?.ToJsonString() ?? "[]");
        if (output.Resolutions.Count != requests.Count || requests.Any(r => output.Resolutions.Count(p => p.ReferenceId == r.ReferenceId) != 1))
            errors.Add("Missing, duplicate or unexpected reference result");
        foreach (var proposal in output.Resolutions)
        {
            var verified = validated.SingleOrDefault(r => r.ReferenceId == proposal.ReferenceId);
            if (verified == null || verified.Status != proposal.Status || !JsonNode.DeepEquals(verified.ResolvedValue, proposal.ResolvedValue))
                errors.Add("Reference interpretation did not pass evidence validation: " + proposal.ReferenceId);
        }

        if (output.Disposition == "search")
        {
            if (input["canSearchAgain"]?.GetValue<bool>() != true || !output.Queries.Any(q => !string.IsNullOrWhiteSpace(q)) || output.Answer != null)
                errors.Add("Search must be allowed, have a query and withhold the answer");
        }
        else if (output.Disposition is "answer" or "clarify")
        {
            if (string.IsNullOrWhiteSpace(output.Answer))
                errors.Add("Missing answer or clarification");
            if (output.Queries.Count != 0)
                errors.Add("A final answer cannot also request a search");
            if (output.Disposition == "answer" && requests.Count > 0 && validated.Any(r => r.Status != "resolved"))
                errors.Add("Unresolved requested reference requires clarification");
            if (output.Disposition == "answer" && sources.Count > 0 && output.AnswerEvidence.Count == 0)
                errors.Add("An evidence-based answer needs original source attribution");
        }
        else
            errors.Add("Invalid answer disposition");
        foreach (var e in output.AnswerEvidence)
            if (!sources.TryGetValue(e.SourceId, out var original) || string.IsNullOrWhiteSpace(e.Quote) || !original.Contains(e.Quote, StringComparison.Ordinal))
                errors.Add("Answer evidence is missing or quote does not occur in supplied source: " + e.SourceId);
        var cited = Regex.Matches(output.Answer ?? "", @"[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}").Select(m => Guid.Parse(m.Value)).ToHashSet();
        var declared = output.AnswerEvidence.Select(e => e.SourceId).ToHashSet();
        if (cited.Except(declared).Any())
            errors.Add("Answer cites a source not declared in its validated evidence");
        return errors.Distinct().ToList();
    }

    // Call only after successful validation. Missing display citations can be
    // rendered from verified IDs without another model call or an invented source.
    public static string Render(ContextAnswerOutput output)
    {
        var answer = output.Answer ?? throw new InvalidOperationException("Search proposals have no answer to display");
        var missing = output.AnswerEvidence.Select(e => e.SourceId).Distinct().Where(id => !answer.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
        return answer + (missing.Count == 0 ? "" : " " + string.Join("", missing.Select(id => $"【{id}】")));
    }
}
