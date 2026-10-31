using System.Text.RegularExpressions;

namespace Mintokei.Memory;

public record RetrievalCoverage(List<SearchAttempt> Attempts, bool CurrentConversationOnly, bool HasLegacyUnknown = false)
{
    public string AllowedSearchScope => CurrentConversationOnly ? "current_conversation" : "authorized_user_history";
    public string CurrentConversationMessages => State("messages", false);
    public string OlderConversationMessages => CurrentConversationOnly ? "restricted" : State("messages", true);
    public string SavedMemories => CurrentConversationOnly ? "restricted" : State("memories", true);

    private string State(string source, bool crossConversation)
    {
        var attempts = Attempts.Where(a => a.Source.StartsWith(source, StringComparison.Ordinal) && (!crossConversation || a.Scope == "authorized_user_history")).ToList();
        if (attempts.Count == 0)
            return HasLegacyUnknown ? "unknown" : "not_searched";
        if (attempts.Any(a => a.Status == "failed"))
            return attempts.Any(a => a.Status == "completed") ? "partially_checked" : "failed";
        return attempts.Any(a => a.Status == "completed") ? "checked" : "skipped";
    }
}
