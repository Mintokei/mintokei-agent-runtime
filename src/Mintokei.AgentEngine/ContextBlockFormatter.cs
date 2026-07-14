namespace Mintokei.AgentEngine;

/// <summary>
/// Formats a workspace context block together with the user's message into the wire text a CLI
/// expects on the first turn. The embedder loads the context block from its own storage and hands
/// it in via <see cref="SessionTurn.ContextBlock"/>; the engine only does the (pure) formatting.
/// </summary>
internal static class ContextBlockFormatter
{
    public static string FormatMessageWithContext(string contextBlock, string userMessage)
        => $"{contextBlock}\n<user-message>\n{userMessage}\n</user-message>";
}
