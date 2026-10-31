using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public static class SchemaFiles
{
    public static string BindSourceIds(string schema, string operation, object input)
    {
        if (operation is "context-evaluation" or "context-answer")
        {
            var evaluationContract = Json.Node(schema)!;
            if (operation == "context-evaluation" && evaluationContract["properties"]?["selection"] == null)
                return schema; // Immutable v1 contract.
            var evaluationInput = Json.Node(Json.Write(input))!;
            var memories = evaluationInput["memories"]?.AsArray().Select(m => m!["id"]!.ToString()).Distinct().ToList() ?? [];
            var history = (evaluationInput["historicalEvidence"]?.AsArray().Select(m => m!["id"]!.ToString()) ?? []).Concat(evaluationInput["rawHistory"]?.AsArray().Where(m => m?["sourceType"]?.ToString() == "message").Select(m => m!["sourceId"]!.ToString()) ?? []).Distinct().ToList();
            void BindArray(System.Text.Json.Nodes.JsonNode array, List<string> ids)
            {
                if (ids.Count == 0)
                    array["maxItems"] = 0;
                else
                    array["items"]!["enum"] = new System.Text.Json.Nodes.JsonArray(ids.Select(x => (System.Text.Json.Nodes.JsonNode? )System.Text.Json.Nodes.JsonValue.Create(x)).ToArray());
            }

            if (operation == "context-evaluation")
            {
                BindArray(evaluationContract["properties"]!["selection"]!["properties"]!["memoryIds"]!, memories);
                BindArray(evaluationContract["properties"]!["selection"]!["properties"]!["messageIds"]!, history);
            }
            else
            {
                var answerSources = ContextAnswer.Sources(evaluationInput.AsObject()).Keys.Select(x => x.ToString()).ToList();
                var answerEvidence = evaluationContract["properties"]!["answerEvidence"]!;
                if (answerSources.Count == 0)
                    answerEvidence["maxItems"] = 0;
                else
                    answerEvidence["items"]!["properties"]!["sourceId"]!["enum"] = new System.Text.Json.Nodes.JsonArray(answerSources.Select(x => (System.Text.Json.Nodes.JsonNode? )System.Text.Json.Nodes.JsonValue.Create(x)).ToArray());
            }

            var evidence = history.Concat(evaluationInput["recentMessages"]?.AsArray().Select(m => m!["id"]!.ToString()) ?? []).Concat(evaluationInput["conversationHistory"]?.AsArray().Select(m => m!["id"]!.ToString()) ?? []).Append(evaluationInput["originalMessage"]!["id"]!.ToString()).Distinct().ToList();
            var resolution = evaluationContract["properties"]!["resolutions"]!["items"]!["properties"]!;
            resolution["sourceMessageId"]!["enum"] = new System.Text.Json.Nodes.JsonArray(evaluationInput["originalMessage"]!["id"]!.ToString());
            resolution["evidence"]!["items"]!["properties"]!["messageId"]!["enum"] = new System.Text.Json.Nodes.JsonArray(evidence.Select(x => (System.Text.Json.Nodes.JsonNode? )System.Text.Json.Nodes.JsonValue.Create(x)).ToArray());
            return evaluationContract.ToJsonString();
        }

        if (operation is not ("extraction" or "state-update"))
            return schema;
        var data = Json.Node(Json.Write(input))!;
        var fresh = data["newMessages"]?.AsArray().Select(m => m!["id"]!.ToString()).ToList() ?? [];
        var ids = fresh.Concat(data["recentMessages"]?.AsArray().Select(m => m!["id"]!.ToString()) ?? []).Concat(data["resolvedReferences"]?.AsArray().Where(r => r?["status"]?.ToString() == "resolved").SelectMany(r => r!["evidence"]!.AsArray().Select(e => e!["messageId"]!.ToString())) ?? []).Distinct().ToList();
        if (ids.Count == 0)
            return schema;
        var contract = Json.Node(schema)!;
        System.Text.Json.Nodes.JsonArray Values(IEnumerable<string> values) => new(values.Select(v => (System.Text.Json.Nodes.JsonNode? )System.Text.Json.Nodes.JsonValue.Create(v)).ToArray());
        contract["properties"]!["stateChanges"]!["items"]!["properties"]!["sourceMessageIds"]!["items"]!["enum"] = Values(ids);
        if (operation == "extraction")
        {
            contract["properties"]!["memoryCandidates"]!["items"]!["properties"]!["sourceEvidence"]!["items"]!["properties"]!["messageId"]!["enum"] = Values(ids);
            if (fresh.Count > 0)
                contract["properties"]!["unresolvedReferences"]!["items"]!["properties"]!["sourceMessageId"]!["enum"] = Values(fresh);
        }

        return contract.ToJsonString();
    }

    public static string PathFor(string root, string operation, string version)
    {
        if (!Regex.IsMatch(operation, "^[a-z-]+$") || !Regex.IsMatch(version, "^[a-zA-Z0-9_-]{1,50}$"))
            throw new ArgumentException("Invalid schema operation or version");
        return Path.Combine(root, "schemas", version == "v1" ? "" : version, operation + ".json");
    }
}
