using System.Text.Json.Nodes;

namespace Mintokei.Memory;
// Projection changes model input only. Full original records remain in PostgreSQL
// and in the compaction span; selectors can only name supplied, authorized IDs.
public static class ContextPacking
{
    public static JsonObject PackLegacy(object input, int characterBudget)
    {
        var data = Json.Node(Json.Write(input))!.AsObject();
        foreach (var key in new[]
        {
            "rawHistory",
            "memories",
            "historicalEvidence",
            "artifacts",
            "knowledge"
        }

        )
        {
            var rows = data[key]?.AsArray();
            while (data.ToJsonString().Length > characterBudget && rows?.Count > 0)
            {
                var last = rows[^1];
                rows.RemoveAt(rows.Count - 1);
                if (key == "memories" && data["sourceLinks"] is JsonArray sources)
                    for (var index = sources.Count - 1; index >= 0; index--)
                        if (sources[index]?["memoryId"]?.ToString() == last?["id"]?.ToString())
                            sources.RemoveAt(index);
                if (key == "artifacts")
                    data["missing"]!.AsArray().Add("Artifact content excluded by context budget.");
            }
        }

        if (data.ToJsonString().Length > characterBudget)
            throw new InvalidOperationException("Required context exceeds the configured token budget; raise the budget or reduce state size.");
        return data;
    }

    private static JsonObject Pick(JsonNode row, params string[] keys)
    {
        var result = new JsonObject();
        foreach (var key in keys)
            if (row[key] is { } value)
                result[key] = value.DeepClone();
        return result;
    }

    private static List<JsonNode> Rows(JsonObject data, string key) => data[key]?.AsArray().Where(n => n != null).Select(n => n!).ToList() ?? [];
    private static string Id(JsonNode row) => (row["id"] ?? row["sourceId"])!.ToString();
    private static void Set(JsonObject data, string key, IEnumerable<JsonNode> rows) => data[key] = new JsonArray(rows.Select(n => n.DeepClone()).ToArray());
    private static JsonObject Message(JsonNode row)
    {
        var result = Pick(row, "id", "conversationId", "sequenceNumber", "role", "content", "createdAt", "provisional");
        if (row["role"]?.ToString() == "tool")
            result["verifiedExecution"] = row["verifiedExecution"]?.DeepClone() ?? Json.Node(row["metadataJson"]?.ToString() ?? "{}")?["verifiedExecution"]?.DeepClone() ?? JsonValue.Create(false);
        return result;
    }

    public static JsonObject Compact(object input)
    {
        var data = Json.Node(Json.Write(input))!.AsObject();
        foreach (var key in new[]
        {
            "originalMessage",
            "newMessage"
        }

        )
            if (data[key] is { } row)
                data[key] = Message(row);
        foreach (var key in new[]
        {
            "recentMessages",
            "historicalEvidence",
            "conversationHistory"
        }

        )
            if (data[key] != null)
                Set(data, key, Rows(data, key).DistinctBy(Id).Select(Message));
        if (data["memories"] != null)
            Set(data, "memories", Rows(data, "memories").DistinctBy(Id).Select(m =>
            {
                var row = Pick(m, "id", "type", "subject", "predicate", "canonicalText", "status", "scope", "validFrom", "validTo", "value");
                // Preserve structured values and qualifiers, even when the canonical text is shorter.
                if (m["valueJson"] is { } value)
                    row["value"] = Json.Node(value.ToString());
                return row;
            }));
        if (data["sourceLinks"] != null)
            Set(data, "sourceLinks", Rows(data, "sourceLinks").DistinctBy(s => (s["memoryId"]?.ToString(), s["sourceId"]?.ToString(), s["sourceRole"]?.ToString(), s["evidenceQuote"]?.ToString())).Select(s => Pick(s, "memoryId", "sourceId", "sourceType", "sourceRole", "evidenceQuote")));
        if (data["resolvedReferences"] != null)
            Set(data, "resolvedReferences", Rows(data, "resolvedReferences").Select(r => Pick(r, "referenceId", "sourceMessageId", "expression", "status", "resolvedValue", "evidence", "explanation")));
        if (data["searchCoverage"] is JsonObject coverage)
            data["searchCoverage"] = Pick(coverage, "currentConversationOnly", "hasLegacyUnknown", "allowedSearchScope", "currentConversationMessages", "olderConversationMessages", "savedMemories");
        if (data["knowledge"] != null)
            Set(data, "knowledge", Rows(data, "knowledge").Select(k => Pick(k, "id", "kind", "name", "status", "memoryId", "dataJson", "createdAt")));
        if (data["rawHistory"] != null)
        {
            var evidence = Rows(data, "historicalEvidence").ToDictionary(Id);
            var remaining = new List<JsonNode>();
            foreach (var raw in Rows(data, "rawHistory").DistinctBy(Id))
            {
                var metadata = Json.Node(raw["metadataJson"]?.ToString() ?? "{}");
                if (evidence.TryGetValue(Id(raw), out var existing))
                {
                    if (metadata?["provisional"]?.GetValue<bool>() == true)
                        existing["provisional"] = true;
                }
                else
                {
                    var row = Pick(raw, "sourceId", "sourceType", "conversationId", "sequenceNumber", "timestamp", "contentExcerpt");
                    if (metadata?["provisional"] is { } flag)
                        row["provisional"] = flag.DeepClone();
                    if (metadata?["role"] is { } role)
                        row["role"] = role.DeepClone();
                    remaining.Add(row);
                }
            }

            Set(data, "rawHistory", remaining);
        }

        return data;
    }

    public static PackedContext Pack(object input, EvidenceSelection? selection, IEnumerable<Guid> memoryRanking, IEnumerable<Guid> protectedMessageIds, int characterBudget)
    {
        var original = Json.Node(Json.Write(input))!.AsObject();
        var data = original.DeepClone().AsObject();
        var allMemories = Rows(data, "memories").DistinctBy(Id).ToList();
        var allEvidence = Rows(data, "historicalEvidence").DistinctBy(Id).ToList();
        var allRaw = Rows(data, "rawHistory").DistinctBy(Id).ToList();
        var allSources = Rows(data, "sourceLinks");
        var rank = memoryRanking.Distinct().Select((id, index) => (id: id.ToString(), index)).ToDictionary(x => x.id, x => x.index);
        allMemories = allMemories.OrderBy(m => rank.GetValueOrDefault(Id(m), int.MaxValue)).ToList();
        var memoryIds = allMemories.Select(Id).ToHashSet();
        var evidenceIds = allEvidence.Concat(allRaw).Select(Id).ToHashSet();
        var invalid = selection == null ? [] : selection.MemoryIds.Select(x => x.ToString()).Where(x => !memoryIds.Contains(x)).Concat(selection.MessageIds.Select(x => x.ToString()).Where(x => !evidenceIds.Contains(x))).Distinct().ToList();
        var accepted = selection != null && invalid.Count == 0;
        var keptMemories = accepted ? selection!.MemoryIds.Select(x => x.ToString()).ToHashSet() : memoryIds.ToHashSet();
        var requiredMessages = protectedMessageIds.Select(x => x.ToString()).Where(evidenceIds.Contains).ToHashSet();
        foreach (var reference in Rows(data, "resolvedReferences"))
            foreach (var e in reference["evidence"]?.AsArray() ?? [])
                if (e?["messageId"] is { } source && evidenceIds.Contains(source.ToString()))
                    requiredMessages.Add(source.ToString());
        if (accepted)
            requiredMessages.UnionWith(selection!.MessageIds.Select(x => x.ToString()));
        // Keep alternate versions and one-hop memories sharing original evidence.
        // A later summary can contradict a selected fact under a different predicate.
        string Family(JsonNode m) => Json.Write(new[] { m["subject"]?.ToString(), m["predicate"]?.ToString(), m["scope"]?.ToString() });
        var families = allMemories.Where(m => keptMemories.Contains(Id(m)) && !string.IsNullOrWhiteSpace(m["predicate"]?.ToString())).Select(Family).ToHashSet();
        keptMemories.UnionWith(allMemories.Where(m => families.Contains(Family(m))).Select(Id));
        var selectedSourcesIds = allSources.Where(s => keptMemories.Contains(s["memoryId"]!.ToString()) && s["sourceType"]?.ToString() == "message").Select(s => s["sourceId"]!.ToString()).Where(evidenceIds.Contains).ToHashSet();
        selectedSourcesIds.UnionWith(requiredMessages); // Also protect interpretations of explicitly selected/cited messages.
        var relatedIds = allSources.Where(s => s["sourceType"]?.ToString() == "message" && selectedSourcesIds.Contains(s["sourceId"]!.ToString())).Select(s => s["memoryId"]!.ToString()).Where(memoryIds.Contains).ToHashSet();
        keptMemories.UnionWith(relatedIds); // Bounded one hop; do not recursively expand the graph.
        var requiredMemories = accepted ? keptMemories.ToHashSet() : new HashSet<string>();
        var keptMessages = accepted ? requiredMessages.ToHashSet() : evidenceIds.ToHashSet();
        foreach (var source in allSources.Where(s => keptMemories.Contains(s["memoryId"]!.ToString())))
            if (evidenceIds.Contains(source["sourceId"]!.ToString()))
                keptMessages.Add(source["sourceId"]!.ToString());
        // A short answer is often meaningful only with its preceding question.
        var selectedSources = allEvidence.Where(m => keptMessages.Contains(Id(m))).ToList();
        foreach (var message in selectedSources.Where(m => m["role"]?.ToString() == "user"))
        {
            var previous = allEvidence.FirstOrDefault(m => m["conversationId"]?.ToString() == message["conversationId"]?.ToString() && m["sequenceNumber"]?.GetValue<long>() == message["sequenceNumber"]?.GetValue<long>() - 1 && m["role"]?.ToString() == "assistant");
            if (previous != null)
                keptMessages.Add(Id(previous));
        }

        if (accepted)
            requiredMessages.UnionWith(keptMessages);
        void Materialize()
        {
            Set(data, "memories", allMemories.Where(m => keptMemories.Contains(Id(m))));
            Set(data, "sourceLinks", allSources.Where(s => keptMemories.Contains(s["memoryId"]!.ToString())));
            Set(data, "historicalEvidence", allEvidence.Where(m => keptMessages.Contains(Id(m))));
            Set(data, "rawHistory", allRaw.Where(m => keptMessages.Contains(Id(m))));
        }

        Materialize();
        var packed = Compact(data);
        var trimmed = new List<string>();
        while (packed.ToJsonString().Length > characterBudget)
        {
            var removable = allMemories.LastOrDefault(m => keptMemories.Contains(Id(m)) && !requiredMemories.Contains(Id(m)));
            if (removable != null)
            {
                var family = Family(removable);
                var group = allMemories.Where(m => Id(m) == Id(removable) || !string.IsNullOrWhiteSpace(removable["predicate"]?.ToString()) && Family(m) == family).Select(Id).ToList();
                if (group.Any(requiredMemories.Contains))
                    throw new InvalidOperationException("Required evidence exceeds the context budget; increase the context budget.");
                foreach (var id in group)
                {
                    keptMemories.Remove(id);
                    trimmed.Add(id);
                }
            }
            else
            {
                var linked = allSources.Where(s => keptMemories.Contains(s["memoryId"]!.ToString())).Select(s => s["sourceId"]!.ToString()).ToHashSet();
                var drop = allEvidence.Concat(allRaw).LastOrDefault(m => keptMessages.Contains(Id(m)) && !requiredMessages.Contains(Id(m)) && !linked.Contains(Id(m)));
                if (drop == null)
                    throw new InvalidOperationException("Required evidence exceeds the context budget; increase the context budget. No cited or selected evidence was silently discarded.");
                keptMessages.Remove(Id(drop));
                trimmed.Add(Id(drop));
            }

            Materialize();
            packed = Compact(data);
        }

        if (invalid.Count > 0)
        {
            var warnings = packed["warnings"]?.AsArray() ?? new JsonArray();
            warnings.Add("Invalid context selection rejected; authorized candidates were retained.");
            packed["warnings"] = warnings.DeepClone();
        }

        var report = Json.Node(Json.Write(new { mode = "compact", selectionStatus = selection == null ? "not_requested" : accepted ? "accepted" : "rejected", invalidIds = invalid, beforeCharacters = original.ToJsonString().Length, afterCharacters = packed.ToJsonString().Length, retainedMemoryIds = Rows(packed, "memories").Select(Id), omittedMemoryIds = memoryIds.Except(keptMemories), retainedMessageIds = keptMessages, omittedMessageIds = evidenceIds.Except(keptMessages), protectedMessageIds = requiredMessages, budgetTrimmedIds = trimmed, sharedSourceMemoryIds = relatedIds, rules = new[] { "authorized supplied IDs only", "retrieval rank preserved", "original evidence and reference sources retained", "same-field temporal alternatives retained", "one-hop shared-source alternatives retained", "preceding questions retained", "metadata projected; full records kept in trace" } }))!.AsObject();
        return new(packed, report);
    }
}
