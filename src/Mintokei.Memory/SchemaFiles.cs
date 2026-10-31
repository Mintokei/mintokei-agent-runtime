using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public static class SchemaFiles
{
    public static string BindSourceIds(string schema, string operation, object input)
    {
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
