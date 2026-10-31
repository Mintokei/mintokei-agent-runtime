namespace Mintokei.Memory;

public static class LlmProviders
{
    public static bool IsCodex(string provider) => provider is "codex_cli" or "codex_app_server";
    public static bool PrepareSession(Conversation conversation, string provider)
    {
        var metadata = Json.Node(conversation.MetadataJson)!.AsObject();
        var previous = metadata["codexSessionProvider"]?.ToString() ?? "codex_cli";
        // Different Codex transports have separate session stores. Rehydrate a new
        // thread from authorized originals after switching, even if a turn fails.
        if (conversation.CodexThreadId != null && (!IsCodex(provider) || previous != provider))
        {
            conversation.CodexThreadId = null;
            metadata["codexSessionProvider"] = provider;
            conversation.MetadataJson = metadata.ToJsonString();
            return true;
        }

        return false;
    }

    public static void RecordSession(Conversation conversation, string provider, string? threadId)
    {
        conversation.CodexThreadId = IsCodex(provider) ? threadId : null;
        var metadata = Json.Node(conversation.MetadataJson)!.AsObject();
        metadata["codexSessionProvider"] = provider;
        conversation.MetadataJson = metadata.ToJsonString();
    }
}
